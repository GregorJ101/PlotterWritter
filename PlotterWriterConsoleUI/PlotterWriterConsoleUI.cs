﻿using System;                         // DateTime, STAThreadAttribute, STAThreadAttributeAttribute
using System.Collections.Generic;     // IEnumerable
using System.Diagnostics;             // Debug
using System.Drawing;                 // Point
using System.IO;                      // Path, Directory
using System.Text;                    // StringBuilder
using System.Threading;               // Sleep
using System.Threading.Tasks;         // async & await
using System.Windows.Forms;           // FolderBrowserDialog

using PlotterEngine;
using HPGL;

using CompilerVersion;


namespace PlotterWriterConsoleUI
{
    class CPlotterWriterConsoleUI
    {
        #region Data Members
        enum EQueueDetailLevel
        {
            ENoDetail,
            EDetailNoHPGL,
            EDetailWithHPGL
        };

        private string m_strAssemblyTitle       = "";
        private string m_strAssemblyVersion     = "";
        private string m_strCompanyName         = "";
        private string m_strConfiguration       = "";
        private string m_strTargetFramework     = "";
        private string m_strOutputPath          = "";

        private int    m_iLastTrackingSize      = 0;
        private int    m_iLastBufferByteCount     = 0;

        private bool   m_bFirstOutputSelection  = true;
        private bool   m_bQueueHasBeenEmptied   = false;
        private bool   m_bKeepOutputThreadRunning = false;
        private bool   m_bShowPlotterTracking     = false;

        private const  string STRING_VERSION           = "Version=";

        private DateTime m_dtBuildDate = new System.IO.FileInfo (System.Reflection.Assembly.GetExecutingAssembly ().Location).LastWriteTime;
        private DateTime m_dtStartPlotTime = DateTime.Now;

        private double[] m_daFactors1 = { -12, -23, -9, 3, 1 };    // [x] [x] https://www.shelovesmath.com/algebra/advanced-algebra/graphing-polynomials/
        private double[] m_daFactors2 = { 5, 2, 0, -1, -4 };       // [x] [x] https://cnx.org/contents/iCB397KI@8.1:MlR18vDZ@2/4-2-Polynomial-Functions-3-2 (table 4.1 top)
        private double[] m_daFactors3 = { -2, -1, 3, 1, 0, 0, 0 }; // [x] [x] https://cnx.org/contents/iCB397KI@8.1:MlR18vDZ@2/4-2-Polynomial-Functions-3-2 (table 4.1 2nd down)
        private double[] m_daFactors4 = { 3, -4, 0, 2, 0, 1 };     // [x] [x] https://cnx.org/contents/iCB397KI@8.1:MlR18vDZ@2/4-2-Polynomial-Functions-3-2 (table 4.1 3rd down)
        private double[] m_daFactors5 = { -6, 7, 3, 1 };           // [x] [x] https://cnx.org/contents/iCB397KI@8.1:MlR18vDZ@2/4-2-Polynomial-Functions-3-2 (table 4.1 bottom)
        private double[] m_daFactors6 = { 1, 0, 0 };               // [x] [x] https://courses.lumenlearning.com/waymakercollegealgebra/chapter/transformations-of-quadratic-functions/
        private double[] m_daFactors7 = { 1, 0, -8, 0, 10, 6 };    // [x] [x] https://www.ck12.org/algebra/graphing-polynomials/lesson/Finding-and-Defining-Parts-of-a-Polynomial-Function-Graph-ALG-II/

        private List<double[]>   m_ldaFactors            = new List<double[]> ();
        private List<string>     m_lstrAlgebraicFormulae = new List<string> ();
        private List<CPlotEntry> m_lstPlotEntries        = new List<CPlotEntry> ();

        private CPlotterEngine m_objPlotterEngine = null;

        private class CPlotEntry
        {
            public string                       strPlotName          = "";
            public int                          iQueueSize           = 0;
            public SortedList<string, int>      slPrintQueueEntries  = new SortedList<string, int> ();
            public List<string>                 lstrDuplicateEntries = new List<string> ();
        }
        #endregion

        [STAThreadAttribute] // For FolderBrowserDialog
        static void Main (string[] straArgs)
        {
            if (!CGenericMethods.DotNet45Found ())
            {
                Console.WriteLine ("PlotterWriter requires .NET Framework 4.5 or later.");

                Console.WriteLine ("Press any key to close app ...");
                Console.ReadKey (true);
            }

            //UnitTestEmbeddedComments ();
            CPlotterWriterConsoleUI objPlotterWriterConsoleUI = new CPlotterWriterConsoleUI ();
            try
            {
                objPlotterWriterConsoleUI.MainNonStatic (straArgs);
            }
            catch (Exception e)
            {
                objPlotterWriterConsoleUI.m_bKeepOutputThreadRunning = false;

                if (objPlotterWriterConsoleUI.m_objPlotterEngine != null)
                {
                    objPlotterWriterConsoleUI.m_objPlotterEngine.CloseSerialPort ();
                }

                Console.WriteLine ("** Exception in ShowSerialQueueContents (): " + e.Message);
                if (e.InnerException != null)
                {
                    Console.WriteLine ("  " + e.InnerException.Message);
                }
                Console.WriteLine (e.StackTrace);

                Console.WriteLine ("Press any key to close app ...");
                Console.ReadKey (true);
            }
        }

        private void MainNonStatic (string[] straArgs)
        {
            //UnitTestShowQueueContents ();
            m_objPlotterEngine = CPlotterEngine.GetPlotterEngine (CPlotterEngine.EPlotterPort.ENoOutput);

            m_bKeepOutputThreadRunning = true;
            StartTrackThreadAsync ();

            if (!ProcessCommandLine (straArgs))
            {
                return;
            }

            CGenericMethods.ShowVersionInfo (ref m_strAssemblyTitle, ref m_strAssemblyVersion, ref m_strCompanyName, ref m_strConfiguration, ref m_strTargetFramework);

            ShowSettings ();
            Console.WriteLine ();

            LoadPolynomialPresetFactors ();

            while (true)
            {
                int iChoice = ShowMainOptionsGetSelection (m_objPlotterEngine.GetPenCount () > 0                                                &&
                                                           m_objPlotterEngine.GetPlotterPort ()     != CPlotterEngine.EPlotterPort.EUnspecified &&
                                                              (m_objPlotterEngine.GetPlotterPort () != CPlotterEngine.EPlotterPort.ENoOutput    ||
                                                               m_strOutputPath.Length > 0));

                if (iChoice == 0)
                {
                    m_bKeepOutputThreadRunning = false;
                    if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
                    {
                        m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.ENoOutput, false);
                    }
                    return; // Quit
                }
                else if (iChoice                                == 1                                         &&
                         ((m_objPlotterEngine.GetPlotterPort () != CPlotterEngine.EPlotterPort.ESerialPort   && m_objPlotterEngine.GetPlotterPort () != CPlotterEngine.EPlotterPort.EParallelPort))) // ||
                          //(m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   && IsPlotterIdle ())                                                                  ||
                          //(m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort && m_objPlotterEngine.GetQueueSize ()   == 0)))
                {
                    ShowOutputOptionsGetSelection ();
                    ShowSettings ();
                }
                else if (iChoice == 2)
                {
                    ShowPenOptionsGetSelection ();
                    ShowSettings ();
                }
                else if (iChoice == 3)
                {
                    GetOutputFolderSelection ();
                    ShowSettings ();
                }
                else if (iChoice == 4)
                {
                    ShowSortOptionsGetSelection ();
                    ShowSettings ();
                }
                else if (iChoice == 5)
                {
                    ShowStringArtOptions ();
                }
                else if (iChoice == 6)
                {
                    ShowLissajousOptions ();
                }
                else if (iChoice == 7)
                {
                    ShowPolynomialOptions ();
                }
            }
        }

        #region Menu Methods
        private int  ShowMainOptionsGetSelection (bool bShowPlotOptions)
        {
            int iChoice = 0;
            char cHighLimit = bShowPlotOptions ? '7' : '4';

            Console.WriteLine ();
            Console.WriteLine ("Choose from the following options:");
            if ((m_objPlotterEngine.GetPlotterPort () != CPlotterEngine.EPlotterPort.ESerialPort   && m_objPlotterEngine.GetPlotterPort () != CPlotterEngine.EPlotterPort.EParallelPort)) // ||
                //(m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   && IsPlotterIdle ())                                                                 ||
                //(m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort && m_objPlotterEngine.GetQueueSize ()   == 0))
            {
                Console.WriteLine ("  1: Select output mode");
            }
            Console.WriteLine ("  2: Select # pens");
            Console.WriteLine ("  3: Select output folder");
            Console.WriteLine ("  4: Sort mode");
            Console.WriteLine ();

            if (bShowPlotOptions)
            {
                Console.WriteLine ("  5: String Art Patterns");
                Console.WriteLine ("  6: Lissajous Patterns");
                Console.WriteLine ("  7: Polynomial Functions");
                Console.WriteLine ();
            }

            if (m_objPlotterEngine.GetQueueLength () > 0)
            {
                Console.WriteLine ("  C: Clear print queue");
                Console.WriteLine ("  Q: Show queue contents");
                Console.WriteLine ("  D: Show detailed queue contents");
                Console.WriteLine ("  A: Show diagnostic queue contents with HPGL string segment");
            }

            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort &&
                m_objPlotterEngine.IsSerialPortOpen ())
            {
                Console.WriteLine ("  B: Show bytes in plotter buffer");
                Console.WriteLine (m_objPlotterEngine.GetPauseAfterNewPen () ? "  P: No pause after new pen selection" :
                                                                               "  P: Pause after selecting new pen");
            }

            if ( m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort ||
                (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   && m_objPlotterEngine.IsSerialPortOpen ()))
            {
                Console.WriteLine (m_bShowPlotterTracking ? "  T: Disable plot progress tracking" :
                                                            "  T: Enable plot progress tracking");

                Console.WriteLine ();
            }

            Console.WriteLine ("  S: Show settings");
            Console.WriteLine ("  V: Show version");
            Console.WriteLine ("  X: Exit program");
            Console.WriteLine ();
            Console.WriteLine ("Please make your selection ...");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 's' ||
                    cki.KeyChar == 'S')
                {
                    ShowSettings ();
                    return -1;
                }
                else if (cki.KeyChar == 'c' ||
                         cki.KeyChar == 'C')
                {
                    Console.WriteLine ("Clearing Queue ...");

                    //Console.WriteLine ("   m_lstPlotEntries.Clear ()");
                    m_lstPlotEntries.Clear ();

                    //Console.WriteLine ("   CPlotterEngine.ClearPrintQueue ()");
                    CPlotterEngine.ClearPrintQueue ();

                    m_bQueueHasBeenEmptied = true;
                    Console.WriteLine ("Queue length: " + m_objPlotterEngine.GetQueueLength () + "  size: " + m_objPlotterEngine.GetQueueSize ());
                    ShowQueueContents ();
                    ShowBytesInPlotterBuffer ();

                    return -1;
                }
                else if ((cki.KeyChar == 'b'  ||
                          cki.KeyChar == 'B') &&
                         m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort &&
                         m_objPlotterEngine.IsSerialPortOpen ())
                {
                    if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
                    {
                        ShowBytesInPlotterBuffer ();
                    }
                    return -1;
                }
                else if ((cki.KeyChar == 'p'  ||
                          cki.KeyChar == 'P') &&
                         m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort &&
                         m_objPlotterEngine.IsSerialPortOpen ())
                {
                    if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
                    {
                        m_objPlotterEngine.SetPauseAfterNewPen (!m_objPlotterEngine.GetPauseAfterNewPen ());
                    }

                    ShowSettings ();
                    return -1;
                }
                else if (cki.KeyChar == 'q' ||
                         cki.KeyChar == 'Q')
                {
                    ShowQueueContents ();
                    return -1;
                }
                else if ((cki.KeyChar == 't'  ||
                          cki.KeyChar == 'T') &&
                         ( m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort ||
                          (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   && m_objPlotterEngine.IsSerialPortOpen ())))
                {
                    m_bShowPlotterTracking = !m_bShowPlotterTracking;
                    return -1;
                }
                else if (cki.KeyChar == 'd' ||
                         cki.KeyChar == 'D')
                {
                    ShowQueueContents (EQueueDetailLevel.EDetailNoHPGL);
                    return -1;
                }
                else if (cki.KeyChar == 'a' ||
                         cki.KeyChar == 'A')
                {
                    ShowQueueContents (EQueueDetailLevel.EDetailWithHPGL);
                    return -1;
                }
                else if (cki.KeyChar == 'v' ||
                         cki.KeyChar == 'V')
                {
                    CGenericMethods.ShowVersionInfo (ref m_strAssemblyTitle, ref m_strAssemblyVersion, ref m_strCompanyName, ref m_strConfiguration, ref m_strTargetFramework);
                    return -1;
                }
                else if (cki.KeyChar == 'x' ||
                         cki.KeyChar == 'X' ||
                         cki.Key     == ConsoleKey.Escape)
                {
                    if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
                    {
                        if (m_objPlotterEngine.GetBytesInPlotterBuffer () > 0)
                        {
                            Console.WriteLine ("Plotter buffer not empty.\n" +
                                               "Press 'W' to wait or return to main menu");
                                               //"Press 'W' to wait, 'C' to clear buffer, or any other key to close app immediately.");
                            ConsoleKeyInfo ckiClose = Console.ReadKey ();
                            Console.WriteLine ();

                            if (ckiClose.KeyChar == 'w' ||
                                ckiClose.KeyChar == 'W')
                            {
                                Console.WriteLine ("  Press the Escape key to abort wait and return to main menu");

                                while (m_objPlotterEngine.GetBytesInPlotterBuffer () > 0)
                                {
                                    if (Console.KeyAvailable &&
                                        Console.ReadKey ().Key == ConsoleKey.Escape)
                                    {
                                        return -1;
                                    }

                                    Thread.Sleep (500);
                                    if (!m_bShowPlotterTracking)
                                    {
                                        ShowBytesInPlotterBuffer ();
                                    }
                                }
                                
                                return 0;
                            }
                            else
                            {
                                return -1;
                            }
                        }
                    }

                    return 0;
                }
                else if (cki.Key == ConsoleKey.Enter)
                {
                    return -1; // Just redisplay the main menu
                }
                else if (cki.KeyChar >= '0' && cki.KeyChar <= cHighLimit)
                {
                    iChoice = (int)(cki.KeyChar - '0');
                    break;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }

            return iChoice;
        }

        private void ShowOutputOptionsGetSelection ()
        {
            Console.WriteLine ("Current output port: " + (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EAutoDetect   ? "Auto-detect"   :
                                                          m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   ? "Serial port:"  :
                                                          m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort ? "Parallel port" :
                                                          m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ENoOutput     ? "No output"     : "Not specified"));
            Console.WriteLine ("Enter one of the following output options:");
            Console.WriteLine ("  S: Serial port");
            Console.WriteLine ("  P: Parallel port");
            Console.WriteLine ("  A: Audo-detect output port");
            Console.WriteLine ("  N: No plotter output, file only");
            Console.WriteLine ("  X: Make no change");

            CPlotterEngine.EPlotterPort eOldPlotterPort = m_objPlotterEngine.GetPlotterPort ();

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar == 's' ||
                         cki.KeyChar == 'S')
                {
                    m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.ESerialPort);
                    if (!m_bFirstOutputSelection                                &&
                        m_objPlotterEngine.GetPlotterPort () != eOldPlotterPort &&
                        eOldPlotterPort != CPlotterEngine.EPlotterPort.EUnspecified)
                    {
                        Console.WriteLine ("The plotter must be restarted before the new port can be used.");
                    }
                    break;
                }
                else if (cki.KeyChar == 'p' ||
                         cki.KeyChar == 'P')
                {
                    m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.EParallelPort);
                    string strParallelPort = m_objPlotterEngine.GetPortName ();
                    if (strParallelPort.Length > 0)
                    {
                        Console.WriteLine ("Found \"" + strParallelPort + "\"");
                        Console.WriteLine ();
                    }
                    if (!m_bFirstOutputSelection                                &&
                        m_objPlotterEngine.GetPlotterPort () != eOldPlotterPort &&
                        eOldPlotterPort != CPlotterEngine.EPlotterPort.EUnspecified)
                    {
                        Console.WriteLine ("The plotter must be restarted before the new port can be used.");
                    }
                    break;
                }
                else if (cki.KeyChar == 'a' ||
                         cki.KeyChar == 'A')
                {
                    m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.EAutoDetect);
                    if (!m_bFirstOutputSelection                                &&
                        m_objPlotterEngine.GetPlotterPort () != eOldPlotterPort &&
                        eOldPlotterPort != CPlotterEngine.EPlotterPort.EUnspecified)
                    {
                        Console.WriteLine ("The plotter must be restarted before the new port can be used.");
                    }
                    break;
                }
                else if (cki.KeyChar == 'n' ||
                         cki.KeyChar == 'N')
                {
                    m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.ENoOutput);
                    if (m_strOutputPath.Length < 1)
                    {
                        Console.WriteLine ("An output path must be specified before any plots can be selected.");
                    }
                    break;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }

            m_bFirstOutputSelection = false;
        }

        private void ShowPenOptionsGetSelection ()
        {
            //Console.WriteLine ("Current setting: " + s_iPenCount.ToString () + " pens" + (m_objPlotterEngine.GetPenSelect () == EPenSelect.ESelectAllPens   ? " used sequentially" :
            //                                                                              m_objPlotterEngine.GetPenSelect () == EPenSelect.ESelectPenRandom ? " selected randomly" : ""));
            Console.WriteLine ("Current setting: " + m_objPlotterEngine.GetPenCount ().ToString () + " pens" +
                               (m_objPlotterEngine.GetSequentialPen () ? " used sequentially" :
                                m_objPlotterEngine.GetRandomPen ()     ? " selected randomly" : ""));
            Console.WriteLine ("Enter the number of pens in the plotter, from 1 to 8, or 'X' for no change:");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar >= '1' && cki.KeyChar <= '8')
                {
                    m_objPlotterEngine.SetPenCount ((int)cki.KeyChar - '0');
                    m_objPlotterEngine.ClearPens ();
                    break;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }

            if (m_objPlotterEngine.GetPenCount () > 1)
            {
                Console.WriteLine ("To select pens randomly, enter 'r', 's' to cycle through all pens, or any other key to skip:");

                while (true)
                {
                    ConsoleKeyInfo cki = Console.ReadKey ();
                    Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                    if (cki.KeyChar == 'r' ||
                        cki.KeyChar == 'R')
                    {
                        m_objPlotterEngine.SetRandomPen (true);
                        m_objPlotterEngine.SetSequentialPen (false);
                        break;
                    }
                    else if (cki.KeyChar == 's' ||
                             cki.KeyChar == 'S')
                    {
                        m_objPlotterEngine.SetRandomPen (false);
                        m_objPlotterEngine.SetSequentialPen (true);
                        break;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        private void GetOutputFolderSelection ()
        {
            Console.WriteLine ("Current folder: " + (m_strOutputPath.Length > 0 ? m_strOutputPath : "<none>"));
            Console.WriteLine ("Enter 'F' to set output folder, 'C' to clear it, or 'X' to make no change.");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'f' ||
                    cki.KeyChar == 'F')
                {
                    FolderBrowserDialog fbd = new FolderBrowserDialog ();
                    fbd.SelectedPath = m_strOutputPath.Length > 0 ? m_strOutputPath : Directory.GetCurrentDirectory ();
                    fbd.ShowDialog ();
                    m_strOutputPath = fbd.SelectedPath;
                    if (m_strOutputPath.Length > 1 &&
                        m_strOutputPath[m_strOutputPath.Length - 1] != '\\')
                    {
                        m_strOutputPath += '\\'; // Inefficient, but probably less so than using StringBuilder just to append a single character
                    }
                    break;
                }
                else if (cki.KeyChar == 'c' ||
                         cki.KeyChar == 'C')
                {
                    m_strOutputPath = "";
                    if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ENoOutput)
                    {
                        Console.WriteLine ("An output path must be specified or the plotter port changed before any plots can be selected.");
                    }
                    break;
                }
                else if (cki.KeyChar == 'x' ||
                         cki.KeyChar == 'X' ||
                         cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }

            if (m_strOutputPath.Length > 1)
            {
                m_objPlotterEngine.SetOutputPath (m_strOutputPath);
            }

            if (m_strOutputPath.Length < 1 &&
                m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ENoOutput)
            {
                Console.WriteLine ("Either an output folder must be specified or a different");
                Console.WriteLine ("output mode selected before any plotting can be done.");
            }
        }

        private void ShowSortOptionsGetSelection ()
        {
            Console.WriteLine ("Current sort setting: " + (m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.EUnsorted               ? "not sorted"                 :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByPenOnly          ? "sort by pen only"           :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByGroupOnly        ? "sort by group only"         :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByPenAndDistance   ? "sort by pen and distance"   :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByGroupAndDistance ? "sort by group and distance" : ""));
            Console.WriteLine ("Enter one of the following output options:");
            Console.WriteLine ("  U: Unsorted");
            Console.WriteLine ("  P: Sort by pen only");
            Console.WriteLine ("  G: Sort by group only");
            Console.WriteLine ("  D: Sort by pen and distance");
            Console.WriteLine ("  E: Sort by group and distance");
            Console.WriteLine ("  X: Make no change");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar == 'u' ||
                         cki.KeyChar == 'U')
                {
                    m_objPlotterEngine.SetSortMode (CPlotterEngine.ESortMode.EUnsorted);
                    break;
                }
                else if (cki.KeyChar == 'p' ||
                         cki.KeyChar == 'P')
                {
                    m_objPlotterEngine.SetSortMode (CPlotterEngine.ESortMode.ESortByPenOnly);
                    break;
                }
                else if (cki.KeyChar == 'g' ||
                         cki.KeyChar == 'G')
                {
                    m_objPlotterEngine.SetSortMode (CPlotterEngine.ESortMode.ESortByGroupOnly);
                    break;
                }
                else if (cki.KeyChar == 'd' ||
                         cki.KeyChar == 'D')
                {
                    m_objPlotterEngine.SetSortMode (CPlotterEngine.ESortMode.ESortByPenAndDistance);
                    break;
                }
                else if (cki.KeyChar == 'e' ||
                         cki.KeyChar == 'E')
                {
                    m_objPlotterEngine.SetSortMode (CPlotterEngine.ESortMode.ESortByGroupAndDistance);
                    break;
                }
                else
                {
                    Console.WriteLine ("Invalied selection.  Please try again ...");
                }
            }
        }

        private void ShowStringArtOptions ()
        {
            Console.WriteLine ("String Art Presets");
            Console.WriteLine ("  1: Simple Triangle 1 (2 pens)"); // (CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_6).hpgl)
            Console.WriteLine ("  2: Simple Triangle 2 (2 pens)"); // (CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_9).hpgl)
            Console.WriteLine ("  3: Simple Triangle 3 (4 pens)"); // (4 images in CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_11).hpgl) (use for defining configurable parameters)
            Console.WriteLine ("  4: Radial Lines (5 pens)");      // (TestPlotterEngine_PlotRadialLines.hpgl)
            Console.WriteLine ("  5: Complex Triangle (3 pens)");  // (TestPlotterEngine_PlotTriangle.hpgl) w/ up to 3 pens
            Console.WriteLine ("  6: Rotated Square (8 pens)");    // (CPlotterTesterEngine_TestPlotter (ROTATE_SQUARE).hpgl)
            Console.WriteLine ("  7: Four Quadrants (8 pens)");    // TestPlotterEngine_PlotFourQuadrants.hpgl
            Console.WriteLine ("  X: Return to main menu");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar == '1')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotSimpleTriangle1 ();
                    InsertPlotEntry ("5-1 PlotSimpleTriangle1", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '2')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotSimpleTriangle2 ();
                    InsertPlotEntry ("5-2 PlotSimpleTriangle2", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '3')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotSimpleTriangle3 ();
                    InsertPlotEntry ("5-3 PlotSimpleTriangle3", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '4')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotRadialLines ();
                    InsertPlotEntry ("5-4 PlotRadialLines", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '5')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotComplexTriangle ();
                    InsertPlotEntry ("5-5 PlotComplexTriangle", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '6')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotRotatedSquare ();
                    InsertPlotEntry ("5-6 PlotRotatedSquare", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '7')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotFourQuadrants ();
                    InsertPlotEntry ("5-7 PlotFourQuadrants", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }
        }

        private void ShowLissajousOptions ()
        {
            Console.WriteLine ("Lissajous Patterns Presets:");
            Console.WriteLine ("  1: Lissajous Pattern 1 (5000x5000, 3:2, 3:5, 3:4) 3 pens");        // CPlotterTesterEngine_TestPlotter (DRAW_LISSAJOUS_2).hpgl (3 patterns, up to 3 pens)
            Console.WriteLine ("  2: Lissajous Pattern 2 (5000x5000, 1:1, 0 -> 180, += 15) 8 pens"); // CPlotterTesterEngine_TestPlotter (DRAW_LISSAJOUS_3).hpgl
            Console.WriteLine ("  3: Lissajous Pattern 3 (5000x5000, 2:3, 0 -> 180, += 30) 3 pens"); // CPlotterTesterEngine_TestPlotter (DRAW_LISSAJOUS_4).hpgl
            Console.WriteLine ("  4: Lissajous Pattern 4 (5000x5000, 4:5, 0 -> 90, += 30) 3 pens");  // CPlotterTesterEngine_TestPlotter (DRAW_LISSAJOUS_5).hpgl
            Console.WriteLine ("  5: Lissajous Pattern 5 (5000x5000, 2:5, 0 -> 180, += 30) 3 pens"); // CPlotterTesterEngine_TestPlotter (DRAW_LISSAJOUS_6).hpgl
            Console.WriteLine ("  X: Return to main menu");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar == '1')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotLissajousPattern1 ();
                    InsertPlotEntry ("6-1 PlotLissajousPattern1", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '2')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotLissajousPattern2 ();
                    InsertPlotEntry ("6-2 PlotLissajousPattern2", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '3')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotLissajousPattern3 ();
                    InsertPlotEntry ("6-3 PlotLissajousPattern3", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '4')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotLissajousPattern4 ();
                    InsertPlotEntry ("6-4 PlotLissajousPattern4", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '5')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotLissajousPattern5 ();
                    InsertPlotEntry ("6-5 PlotLissajousPattern5", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }
        }

        private void ShowPolynomialOptions ()
        {
            Console.WriteLine ("Polynomial Functions Presets:"); // (4 parts, up to 4 pens)
            Console.WriteLine ("  1: Polynomial Chart 1 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 0 ? ": " +m_lstrAlgebraicFormulae[0] : "")); //TestPolynomialChart1.hpgl
            Console.WriteLine ("  2: Polynomial Chart 2 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 1 ? ": " +m_lstrAlgebraicFormulae[1] : "")); //TestPolynomialChart2.hpgl
            Console.WriteLine ("  3: Polynomial Chart 3 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 2 ? ": " +m_lstrAlgebraicFormulae[2] : "")); //TestPolynomialChart3.hpgl
            Console.WriteLine ("  4: Polynomial Chart 4 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 3 ? ": " +m_lstrAlgebraicFormulae[3] : "")); //TestPolynomialChart4.hpgl
            Console.WriteLine ("  5: Polynomial Chart 5 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 4 ? ": " +m_lstrAlgebraicFormulae[4] : "")); //TestPolynomialChart5.hpgl
            Console.WriteLine ("  6: Polynomial Chart 6 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 5 ? ": " +m_lstrAlgebraicFormulae[5] : "")); //TestPolynomialChart6.hpgl
            Console.WriteLine ("  7: Polynomial Chart 7 (4 pens)" + (m_lstrAlgebraicFormulae.Count > 6 ? ": " +m_lstrAlgebraicFormulae[6] : "")); //TestPolynomialChart7.hpgl
            Console.WriteLine ("  X: Return to main menu");

            while (true)
            {
                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                if (cki.KeyChar == 'x' ||
                    cki.KeyChar == 'X' ||
                    cki.Key     == ConsoleKey.Escape)
                {
                    return;
                }
                else if (cki.KeyChar == '1')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart1 ();
                    InsertPlotEntry ("7-1 PlotPolynomialChart1", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '2')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart2 ();
                    InsertPlotEntry ("7-2 PlotPolynomialChart2", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '3')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart3 ();
                    InsertPlotEntry ("7-3 PlotPolynomialChart3", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '4')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart4 ();
                    InsertPlotEntry ("7-4 PlotPolynomialChart4", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '5')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart5 ();
                    InsertPlotEntry ("7-5 PlotPolynomialChart5", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '6')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart6 ();
                    InsertPlotEntry ("7-6 PlotPolynomialChart6", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else if (cki.KeyChar == '7')
                {
                    if (IsPlotterIdle ())
                    {
                        m_dtStartPlotTime = DateTime.Now;
                    }

                    int iQueueLengthBefore = m_objPlotterEngine.GetQueueLength ();
                    int iQueueSizeBefore   = m_objPlotterEngine.GetQueueSize ();
                    PlotPolynomialChart7 ();
                    InsertPlotEntry ("7-7 PlotPolynomialChart7", iQueueLengthBefore, iQueueSizeBefore, true);
                    return;
                }
                else
                {
                    Console.WriteLine ("Invalid selection.  Please try again ...");
                }
            }
        }
        #endregion

        #region String Art
        private void PlotSimpleTriangle1 ()
        {
            // (CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_6).hpgl)
            string strOutputFilename = "SimpleTriangle1.hpgl"; // (2 parts, up to 2 pens)
            PromptNewFilename (ref strOutputFilename);

            // Draw without guidelines
            CComplexLinesShape clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (0,    // int iLine1StartX,
                                                                                                             5000, // int iLine1StartY,
                                                                                                             5000, // int iLine1EndX,
                                                                                                             5000, // int iLine1EndY,
                                                                                                             0,    // int iLine2StartX,
                                                                                                             0,    // int iLine2StartY,
                                                                                                             0,    // int iLine2EndX,
                                                                                                             5000, // int iLine2EndY,
                                                                                                             15),  // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (1), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            // Draw with guidelines
            clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (0,       // int iLine1StartX,
                                                                                          5000,    // int iLine1StartY,
                                                                                          5000,    // int iLine1EndX,
                                                                                          5000,    // int iLine1EndY,
                                                                                          0,       // int iLine2StartX,
                                                                                          0,       // int iLine2StartY,
                                                                                          0,       // int iLine2EndX,
                                                                                          5000,    // int iLine2EndY,
                                                                                          15,      // int iStepCount,
                                                                                          true),   // bool bDrawGuideLines = false)
                                                                         m_objPlotterEngine.GetNextPen (2), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotSimpleTriangle2 ()
        {
            // (CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_9).hpgl)
            string strOutputFilename = "SimpleTriangle2.hpgl"; // (2 parts, up to 2 pens)
            PromptNewFilename (ref strOutputFilename);

            // Draw with guidelines
            CComplexLinesShape clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (0,       // int iLine1StartX,
                                                                                                             5000,    // int iLine1StartY,
                                                                                                             5000,    // int iLine1EndX,
                                                                                                             5000,    // int iLine1EndY,
                                                                                                             5000,    // int iLine2StartX,
                                                                                                             5000,    // int iLine2StartY,
                                                                                                             5000,    // int iLine2EndX,
                                                                                                             0,       // int iLine2EndY,
                                                                                                             15,      // int iStepCount,
                                                                                                             true),   // bool bDrawGuideLines = false)
                                                                         m_objPlotterEngine.GetNextPen (1), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            // Draw without guidelines
            clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (0,    // int iLine1StartX,
                                                                                          5000, // int iLine1StartY,
                                                                                          5000, // int iLine1EndX,
                                                                                          5000, // int iLine1EndY,
                                                                                          5000, // int iLine2StartX,
                                                                                          5000, // int iLine2StartY,
                                                                                          5000, // int iLine2EndX,
                                                                                          0,    // int iLine2EndY,
                                                                                          15),  // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (2), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotSimpleTriangle3 ()
        {
            // (4 images in CPlotterTesterEngine_TestPlotter (DRAW_LINE_ART_11).hpgl) (use for defining configurable parameters)
            string strOutputFilename = "SimpleTriangle3.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);

            // First case:    0, 5000, 2000, 10000, 2000, 10000, 3000, 6000, 15
            CComplexLinesShape clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (0,     // int iLine1StartX,
                                                                                                             5000,  // int iLine1StartY,
                                                                                                             2000,  // int iLine1EndX,
                                                                                                             10000, // int iLine1EndY,
                                                                                                             2000,  // int iLine2StartX,
                                                                                                             10000, // int iLine2StartY,
                                                                                                             3000,  // int iLine2EndX,
                                                                                                             6000,  // int iLine2EndY,
                                                                                                             15),   // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (1), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            // Second case: 4000, 10000, 6000, 5000, 6000, 5000, 7000, 9000, 15
            clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (4000,  // int iLine1StartX,
                                                                                          10000, // int iLine1StartY,
                                                                                          6000,  // int iLine1EndX,
                                                                                          5000,  // int iLine1EndY,
                                                                                          6000,  // int iLine2StartX,
                                                                                          5000,  // int iLine2StartY,
                                                                                          7000,  // int iLine2EndX,
                                                                                          9000,  // int iLine2EndY,
                                                                                          15),   // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (2), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            // Third case: 2000, 6000, 0, 3000, 0, 3000, 2000,    0, 15
            clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (2000, // int iLine1StartX,
                                                                                          6000, // int iLine1StartY,
                                                                                          0,    // int iLine1EndX,
                                                                                          3000, // int iLine1EndY,
                                                                                          0,    // int iLine2StartX,
                                                                                          3000, // int iLine2StartY,
                                                                                          2000, // int iLine2EndX,
                                                                                          0,    // int iLine2EndY,
                                                                                          15),  // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (3), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            // Fourth case: 4000, 6000, 5000, 3000, 5000, 3000, 4000,    0, 15
            clsSteppedLines = new CComplexLinesShape (CPlotterStringArt.PlotSteppedLines (4000, // int iLine1StartX,
                                                                                          6000, // int iLine1StartY,
                                                                                          5000, // int iLine1EndX,
                                                                                          3000, // int iLine1EndY,
                                                                                          5000, // int iLine2StartX,
                                                                                          3000, // int iLine2StartY,
                                                                                          4000, // int iLine2EndX,
                                                                                          0,    // int iLine2EndY,
                                                                                          15),  // int iStepCount,
                                                                         m_objPlotterEngine.GetNextPen (4), false);
            m_objPlotterEngine.AddElement (clsSteppedLines);

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotRadialLines ()
        {
            string strOutputFilename = "RadialLines.hpgl"; // (5 parts, up to 5 pens)
            PromptNewFilename (ref strOutputFilename);

            CComplexLinesShape[] clsaRadialLines = CPlotterStringArt.PlotRadialLines (0,                                  // int iBottom
                                                                                      5000,                               // int iTop
                                                                                      0,                                  // int iLeft
                                                                                      5000,                               // int iRight
                                                                                      10,                                 // int iStepCount
                                                                                      5,                                  // int iThickness
                                                                                      m_objPlotterEngine.GetNextPen (1)); // EPenSelect ePenSelection

            m_objPlotterEngine.AddElements (clsaRadialLines);

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotComplexTriangle ()
        {
            // (TestPlotterEngine_PlotTriangle.hpgl) w/ up to 3 pens
            string strOutputFilename = "ComplexTriangle.hpgl"; // (3 parts, up to 3 pens)
            PromptNewFilename (ref strOutputFilename);

            m_objPlotterEngine.AddElements (CPlotterStringArt.PlotTriangle (0, 0, 0, 5000, 4000, 2500, 15, EPenSelect.ESelectAllPens));

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotRotatedSquare ()
        {
            // (CPlotterTesterEngine_TestPlotter (ROTATE_SQUARE).hpgl)
            string strOutputFilename = "RotatedSquare.hpgl"; // (10 parts, up to 8 pens)
            PromptNewFilename (ref strOutputFilename);

            List<Point> lptSquare = new List<Point> ();
            lptSquare.Add (new Point (1000, 1000));
            lptSquare.Add (new Point (1000, 3000));
            lptSquare.Add (new Point (3000, 3000));
            lptSquare.Add (new Point (3000, 1000));
            lptSquare.Add (new Point (1000, 1000));

            for (int iAngle = 0; iAngle < 90; iAngle += 10)
            {
                Point[] aptRotatedSquare = CPlotterMath.RotatePoints (new Point (2000, 2000), iAngle, lptSquare.ToArray ());
                CComplexLinesShape clsRotatedSquare = new CComplexLinesShape (aptRotatedSquare, m_objPlotterEngine.GetNextPen (1));
                m_objPlotterEngine.AddElement (clsRotatedSquare);
            }

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotFourQuadrants ()
        {
            // TestPlotterEngine_PlotFourQuadrants.hpgl
            string strOutputFilename = "FourQuadrants.hpgl"; // (16 parts, up to 8 pens)
            PromptNewFilename (ref strOutputFilename);

            m_objPlotterEngine.AddElements (CPlotterStringArt.PlotFourQuadrants (0, 1300, -7500, -7500, 16,
                                                                                 EPenSelect.ESelectPenRandom, false,
                                                                                 EPenSelect.ESelectPenRandom, false,
                                                                                 EPenSelect.ESelectPenRandom, false));

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }
        #endregion

        #region Lissajous Patterns
        private void PlotLissajousPattern1 ()
        {
            string strOutputFilename = "LissajousPattern1.hpgl"; // (3 parts, up to 3 pens)
            PromptNewFilename (ref strOutputFilename);

            CComplexLinesShape clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (3,     // int iWaveLengthX
                                                                                                            2,     // int iWaveLengthY
                                                                                                            5000,  // int iAmplitudeX
                                                                                                            5000), // int iAmplitudeY
                                                                      m_objPlotterEngine.GetNextPen (1), false);
            m_objPlotterEngine.AddElement (clsLissajous);

            clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (3,     // int iWaveLengthX
                                                                                         5,     // int iWaveLengthY
                                                                                         5000,  // int iAmplitudeX
                                                                                         5000), // int iAmplitudeY
                                                   m_objPlotterEngine.GetNextPen (2), false);
            m_objPlotterEngine.AddElement (clsLissajous);

            clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (3,     // int iWaveLengthX
                                                                                         4,     // int iWaveLengthY
                                                                                         5000,  // int iAmplitudeX
                                                                                         5000), // int iAmplitudeY
                                                   m_objPlotterEngine.GetNextPen (3), false);
            m_objPlotterEngine.AddElement (clsLissajous);

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotLissajousPattern2 ()
        {
            string strOutputFilename = "LissajousPattern2.hpgl"; // (19 parts, up to 8 pens)
            PromptNewFilename (ref strOutputFilename);

            for (int iPhase = 0; iPhase <= 180; iPhase += 15)
            {
                CComplexLinesShape clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (1,       // int iWaveLengthX
                                                                                                                1,       // int iWaveLengthY
                                                                                                                5000,    // int iAmplitudeX
                                                                                                                5000,    // int iAmplitudeY
                                                                                                                false,   // bool bSwapXandY
                                                                                                                iPhase), // int iPhaseX
                                                                          m_objPlotterEngine.GetNextPen (1), false);
                m_objPlotterEngine.AddElement (clsLissajous);
            }

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotLissajousPattern3 ()
        {
            string strOutputFilename = "LissajousPattern3.hpgl"; // (3 parts, up to 3 pens)
            PromptNewFilename (ref strOutputFilename);

            for (int iPhase = 0; iPhase <= 180; iPhase += 30)
            {
                if (iPhase != 60 &&
                    iPhase != 90 &&
                    iPhase != 150 &&
                    iPhase != 180)
                {
                    CComplexLinesShape clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (2,       // int iWaveLengthX
                                                                                                                    3,       // int iWaveLengthY
                                                                                                                    5000,    // int iAmplitudeX
                                                                                                                    5000,    // int iAmplitudeY
                                                                                                                    false,   // bool bSwapXandY
                                                                                                                    iPhase), // int iPhaseX
                                                                              m_objPlotterEngine.GetNextPen (1), false);
                    m_objPlotterEngine.AddElement (clsLissajous);
                }
            }

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotLissajousPattern4 ()
        {
            string strOutputFilename = "LissajousPattern4.hpgl"; // (3 parts, up to 3 pens)
            PromptNewFilename (ref strOutputFilename);

            for (int iPhase = 0; iPhase < 90; iPhase += 30)
            {
                CComplexLinesShape clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (4,       // int iWaveLengthX
                                                                                                                5,       // int iWaveLengthY
                                                                                                                5000,    // int iAmplitudeX
                                                                                                                5000,    // int iAmplitudeY
                                                                                                                false,   // bool bSwapXandY
                                                                                                                iPhase), // int iPhaseX
                                                                          m_objPlotterEngine.GetNextPen (1), false);
                m_objPlotterEngine.AddElement (clsLissajous);
            }

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PlotLissajousPattern5 ()
        {
            string strOutputFilename = "LissajousPattern5.hpgl"; // (3 parts, up to 3 pens)
            PromptNewFilename (ref strOutputFilename);

            for (int iPhase = 0; iPhase < 180; iPhase += 30)
            {
                if (iPhase != 60 &&
                    iPhase != 90 &&
                    iPhase != 150 &&
                    iPhase != 180)
                {
                    CComplexLinesShape clsLissajous = new CComplexLinesShape (CPlotterStringArt.PlotLissajousCurve (2,       // int iWaveLengthX
                                                                                                                    5,       // int iWaveLengthY
                                                                                                                    5000,    // int iAmplitudeX
                                                                                                                    5000,    // int iAmplitudeY
                                                                                                                    false,   // bool bSwapXandY
                                                                                                                    iPhase), // int iPhaseX
                                                                              m_objPlotterEngine.GetNextPen (1), false);
                    m_objPlotterEngine.AddElement (clsLissajous);
                }
            }

            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }
        #endregion

        #region Polynomial Charts
        private void PlotPolynomialChart1 ()
        {
            string strOutputFilename = "TestPolynomialChart1.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors1, strOutputFilename);
        }

        private void PlotPolynomialChart2 ()
        {
            string strOutputFilename = "TestPolynomialChart2.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors2, strOutputFilename);
        }

        private void PlotPolynomialChart3 ()
        {
            string strOutputFilename = "TestPolynomialChart3.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors3, strOutputFilename);
        }

        private void PlotPolynomialChart4 ()
        {
            string strOutputFilename = "TestPolynomialChart4.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors4, strOutputFilename);
        }

        private void PlotPolynomialChart5 ()
        {
            string strOutputFilename = "TestPolynomialChart5.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors5, strOutputFilename);
        }

        private void PlotPolynomialChart6 ()
        {
            string strOutputFilename = "TestPolynomialChart6.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors6, strOutputFilename);
        }

        private void PlotPolynomialChart7 ()
        {
            string strOutputFilename = "TestPolynomialChart7.hpgl"; // (4 parts, up to 4 pens)
            PromptNewFilename (ref strOutputFilename);
            PlotPolynomialEngine (m_daFactors7, strOutputFilename);
        }
        #endregion

        #region Threading methods
        private async void StartTrackThreadAsync ()
        {
            //Console.WriteLine ("In StartTrackThreadAsync ()");

            string result = await TrackProgressAsync ("TrackProgressAsync");

            //Console.WriteLine ("Await result: " + result);
        }

        private Task<string> TrackProgressAsync (string name)
        {
            //Console.WriteLine (string.Format ("In TrackProgressAsync ({0})", name));

            return Task.Run<string> (() =>
            {
                return TrackProgressThread (name);
            });
        }

        private string TrackProgressThread (string name)
        {
            //Console.WriteLine (string.Format ("In TrackProgressThread ({0})", name));

            while (m_bKeepOutputThreadRunning)
            {
                if ( m_bShowPlotterTracking &&
                    (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   && IsSerialPortOpen () ||
                     m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort && !IsPlotterIdle ()))
                {
                    int iQueueSize = m_objPlotterEngine.GetQueueSize (true);
                    int iBufferByteCount = m_objPlotterEngine.GetBytesInPlotterBuffer ();
                    if ((m_iLastTrackingSize    != iQueueSize        ||
                         m_iLastBufferByteCount != iBufferByteCount) &&
                         m_objPlotterEngine.GetQueueLength () >= 0)
                    {
                        TimeSpan tsElapsed = DateTime.Now - m_dtStartPlotTime;
                        if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
                        {
                            Console.WriteLine (string.Format ("  Plot tracking: {0,2:00}:{1,2:00}  [{2,3}]  [{3,5}]  bytes in plotter buffer: [{4,4}]",
                                                              tsElapsed.Minutes, tsElapsed.Seconds, m_objPlotterEngine.GetQueueLength (),
                                                              iQueueSize, m_objPlotterEngine.GetBytesInPlotterBuffer ()));
                        }
                        else
                        {
                            Console.WriteLine (string.Format ("  Plot tracking: {0,2:00}:{1,2:00}  [{2,3}]  [{3,5}]",
                                                              tsElapsed.Minutes, tsElapsed.Seconds, m_objPlotterEngine.GetQueueLength (), iQueueSize));
                        }
                    }
                    m_iLastTrackingSize = iQueueSize;
                    m_iLastBufferByteCount = iBufferByteCount;
                }

                Thread.Sleep (1000);
            }

            return string.Format ("Closing {0}", name);
        }
        #endregion

        #region Support Methods
        private void PlotPolynomialEngine (double[] daFactors, string strOutputFilename)
        {
            int iZeroPointX  = -1;
            int iZeroPointY  = -1;
            int iIncrement   = -1;

            CDrawingShapeElement[] dsePolynomials = CPlotterStringArt.PlotPolynomialChart (daFactors, -5, 4, .01, ref iZeroPointX, ref iZeroPointY, ref iIncrement,
                                                                                           m_objPlotterEngine.GetNextPen (1), m_objPlotterEngine.GetNextPen (2),
                                                                                           m_objPlotterEngine.GetNextPen (3), m_objPlotterEngine.GetNextPen (4));
            m_objPlotterEngine.AddElements (dsePolynomials);
            m_objPlotterEngine.SortAllEntriesByDistance ();
            m_objPlotterEngine.OutputHPGL (strOutputFilename, m_objPlotterEngine.GetSortMode ());
            m_objPlotterEngine.ClearAll ();
        }

        private void PromptNewFilename (ref string strOutputFilename)
        {
            if (m_strOutputPath.Length > 0)
            {
                Console.WriteLine ("Current filename is \"" + strOutputFilename + "\".  Enter 'C' to change it; any other key to keep it.");

                ConsoleKeyInfo cki = Console.ReadKey ();
                Console.WriteLine (); // Place next message on a separate line after the user-enterec keystroke character

                while (true)
                {
                    if (cki.KeyChar == 'c' ||
                        cki.KeyChar == 'C')
                    {
                        string strNewOutputFilename = Console.ReadLine ();

                        // NTFS Forbids the use of characters in range 1-31 (0x01-0x1F) and characters " * / : < > ? \ | 
                        // https://en.wikipedia.org/wiki/Filename#:~:text=NTFS%20allows%20each%20path%20component,extension%20(for%20example%2C%20AUX.

                        if (strNewOutputFilename.IndexOfAny (Path.GetInvalidFileNameChars ()) >= 0)
                        {
                            // https://stackoverflow.com/questions/4650462/easiest-way-to-check-if-an-arbitrary-string-is-a-valid-filename/4650523
                            Console.WriteLine ("The filename has some invalid characters in it.  Please enter a new one.");
                        }
                        else
                        {
                            int iLastDotIdx = strNewOutputFilename.LastIndexOf ('.');
                            if (iLastDotIdx < 0)
                            {
                                strNewOutputFilename += ".hpgl";
                            }

                            Console.WriteLine ("New Filename: " + strNewOutputFilename);
                            strOutputFilename = strNewOutputFilename;
                            return;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }
        }

        private void LoadPolynomialPresetFactors ()
        {
            m_ldaFactors.Add (m_daFactors1);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors1));

            m_ldaFactors.Add (m_daFactors2);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors2));

            m_ldaFactors.Add (m_daFactors3);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors3));

            m_ldaFactors.Add (m_daFactors4);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors4));

            m_ldaFactors.Add (m_daFactors5);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors5));

            m_ldaFactors.Add (m_daFactors6);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors6));

            m_ldaFactors.Add (m_daFactors7);
            m_lstrAlgebraicFormulae.Add (CreateAlgebraicExpression (m_daFactors7));
        }

        private void InsertPlotEntry (string strPlotName, int iQueueLengthBefore, int iQueueSizeBefore, bool bShowNewEntry = false)
        {
            //Console.WriteLine ("  >> New plot: " + strPlotName + " (" + iQueueSizeBefore.ToString () + ')');

            CHPGL.SPrintQueueEntry[] arPrintQueueJobList = m_objPlotterEngine.GetPrintQueueJobList ();
            m_bQueueHasBeenEmptied = false;

            string strPlotNameRaw = "";
            int iFirstSpaceIdx = strPlotName.IndexOf (' ');
            if (iFirstSpaceIdx > 0 &&
                iFirstSpaceIdx < strPlotName.Length)
            {
                strPlotNameRaw = strPlotName.Substring (iFirstSpaceIdx + 1);
            }

            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
            {
                CPlotEntry objPlotEntry           = new CPlotEntry ();
                m_lstPlotEntries.Add (objPlotEntry);

                foreach (CHPGL.SPrintQueueEntry pqe in arPrintQueueJobList)
                {
                    string strDocName = pqe.strDocumentName;

                    if (objPlotEntry.slPrintQueueEntries.ContainsKey (strDocName))
                    {
                        objPlotEntry.lstrDuplicateEntries.Add (strDocName);
                    }
                    else
                    {
                        int iEntryLength = 0;
                        int iLastUnderscoreIdx = strDocName.LastIndexOf ('_');
                        if (iLastUnderscoreIdx > 0 &&
                            iLastUnderscoreIdx < strDocName.Length)
                        {
                            string strEntryLength = strDocName.Substring (iLastUnderscoreIdx + 1);
                            iEntryLength          = CGenericMethods.SafeConvertToInt (strEntryLength);
                        }

                        objPlotEntry.slPrintQueueEntries.Add (strDocName, iEntryLength);
                    }
                }

                objPlotEntry.strPlotName = strPlotNameRaw;
                objPlotEntry.iQueueSize   = m_objPlotterEngine.GetQueueSize () - iQueueSizeBefore;

                if (bShowNewEntry)
                {
                    //Console.WriteLine ("  New: " + strPlotName + " [" + m_iTestQueueLength + ']');
                    Console.WriteLine (strPlotName                                      + " ["  +
                                       m_objPlotterEngine.GetQueueLength ().ToString () + "] [" +
                                       m_objPlotterEngine.GetQueueSize ().ToString ()   + ']');
                    ShowQueueContents (EQueueDetailLevel.EDetailNoHPGL);
                }
            }
            else if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort)
            {
                //PrinterDriver: USB Parallel Port
                m_objPlotterEngine.ClearOldHPGLStrings ();
                CHPGL.SPrintQueueEntry[] arpqePrintQueueJobList = m_objPlotterEngine.GetPrintQueueJobList ();
                m_lstPlotEntries.Clear ();

                //Console.WriteLine ("Queue length: " + m_objPlotterEngine.GetQueueLength () + "  size: " + m_objPlotterEngine.GetQueueSize ());
                //Console.WriteLine ("SPrintQueueEntry[] length: " + arpqePrintQueueJobList.Length);

                string strLastDocName = "";
                CPlotEntry sPlotEntry = new CPlotEntry ();

                foreach (CHPGL.SPrintQueueEntry pqe in arpqePrintQueueJobList)
                {
                    //Console.WriteLine ("Name: {0}  Size {1}", pqe.strDocumentName, pqe.iDocumentLength);

                    string strDocumentNameSubstring = ExtractPlotName (pqe.strDocumentName);

                    if (strLastDocName == "" ||
                        !strLastDocName.Contains (strDocumentNameSubstring))
                    {
                        if (sPlotEntry.slPrintQueueEntries != null &&
                            sPlotEntry.slPrintQueueEntries.ContainsKey (pqe.strDocumentName))
                        {
                            sPlotEntry.lstrDuplicateEntries.Add (pqe.strDocumentName);
                            Console.WriteLine ("--> Duplicate document entry: " + pqe.strDocumentName             + " [" +
                                                                                  pqe.iDocumentLength.ToString () + ']');
                        }
                        else
                        {
                            // Create new SPlotEntry
                            sPlotEntry = new CPlotEntry ();
                            sPlotEntry.slPrintQueueEntries  = new SortedList<string, int> ();
                            sPlotEntry.lstrDuplicateEntries = new List<string> ();

                            sPlotEntry.strPlotName  = strDocumentNameSubstring;
                            sPlotEntry.iQueueSize   = pqe.iDocumentLength;
                            sPlotEntry.strPlotName  = strDocumentNameSubstring;

                            CHPGL.SPrintQueueEntry sPrintQueueEntry = new CHPGL.SPrintQueueEntry ();
                            sPrintQueueEntry.strDocumentName = strDocumentNameSubstring;
                            sPrintQueueEntry.iDocumentLength = pqe.iDocumentLength;
                            m_lstPlotEntries.Add (sPlotEntry);

                            sPlotEntry.slPrintQueueEntries.Add (pqe.strDocumentName, sPrintQueueEntry.iDocumentLength);

                            strLastDocName = strDocumentNameSubstring;
                        }
                    }
                    else
                    {
                        // Continue adding entries
                        sPlotEntry.slPrintQueueEntries.Add (pqe.strDocumentName, pqe.iDocumentLength);
                        sPlotEntry.iQueueSize += pqe.iDocumentLength;

                        CHPGL.SPrintQueueEntry sPrintQueueEntry = new CHPGL.SPrintQueueEntry ();
                        sPrintQueueEntry.strDocumentName = pqe.strDocumentName;
                        sPrintQueueEntry.iDocumentLength = pqe.iDocumentLength;
                    }
                }

                Console.WriteLine (strPlotName                                      + " ["    +
                                   m_objPlotterEngine.GetQueueLength ().ToString () + "] ["   +
                                   m_objPlotterEngine.GetQueueSize ().ToString ()   + "]   [" +
                                   sPlotEntry.slPrintQueueEntries.Count             + "] [" +
                                   sPlotEntry.iQueueSize                            + ']');

                ShowQueueContents (EQueueDetailLevel.EDetailNoHPGL);

                if (sPlotEntry.lstrDuplicateEntries.Count > 0)
                {
                    Console.WriteLine (sPlotEntry.lstrDuplicateEntries.Count.ToString () + " redundant document entries::");
                }

                foreach (string str in sPlotEntry.lstrDuplicateEntries)
                {
                    Console.WriteLine ("  " + str);
                }
            }
        }

        private bool IsSerialPortOpen ()
        {
            return m_objPlotterEngine != null &&
                   m_objPlotterEngine.IsSerialPortOpen ();
        }

        private bool IsPlotterIdle ()
        {
            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
            {
                return m_objPlotterEngine                            != null &&
                       m_objPlotterEngine.IsSerialPortOpen ()                &&
                       m_objPlotterEngine.GetQueueLength ()          == 0    &&
                       m_objPlotterEngine.GetQueueSize ()            == 0    &&
                       m_objPlotterEngine.GetBytesInPlotterBuffer () == 0;
            }
            else if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort)
            {
                return m_objPlotterEngine.GetQueueLength () == 0;
            }

            return false;
        }

        private void ShowBytesInPlotterBuffer ()
        {
            Console.WriteLine (m_objPlotterEngine.GetBytesInPlotterBuffer ().ToString () + " bytes in plotter buffer");
        }

        private void ShowQueueContents (EQueueDetailLevel eQueueDetailLevel = EQueueDetailLevel.ENoDetail)
        {
            if (m_bQueueHasBeenEmptied ||
                m_objPlotterEngine.GetQueueLength () == 0)
            {
                Console.WriteLine ("Queue is empty");
                return;
            }

            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
            {
                ShowSerialQueueContents (eQueueDetailLevel);
            }
            else if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort)
            {
                ShowParallelQueueContents (eQueueDetailLevel);
            }
        }

        private void ShowParallelQueueContents (EQueueDetailLevel eQueueDetailLevel)
        {
            RebuildInternalQueue ();

            if (m_objPlotterEngine.GetQueueLength () == 0)
            {
                Console.WriteLine ("Queue is empty");
                return;
            }

            int iIdx = 0;
            foreach (CPlotEntry spe in m_lstPlotEntries)
            {
                Console.WriteLine ();

                if (eQueueDetailLevel == EQueueDetailLevel.EDetailNoHPGL)
                {
                    Console.WriteLine (spe.strPlotName + " [" +
                                       CGenericMethods.FillStringLength (spe.slPrintQueueEntries.Count.ToString (), 5, ' ') + "]                             [" +
                                       CGenericMethods.FillStringLength (spe.iQueueSize.ToString (), 5, ' ') + ']');
                    foreach (KeyValuePair<string, int> kvp in spe.slPrintQueueEntries)
                    {
                        Console.WriteLine ("  " + kvp.Key + " [" + CGenericMethods.FillStringLength (kvp.Value.ToString (), 5, ' ') + ']');
                    }
                }
                else if (eQueueDetailLevel == EQueueDetailLevel.EDetailWithHPGL)
                {
                    Console.WriteLine (spe.strPlotName + " [" +
                                       CGenericMethods.FillStringLength (spe.slPrintQueueEntries.Count.ToString (), 5, ' ') + "]                             [" +
                                       CGenericMethods.FillStringLength (spe.iQueueSize.ToString (), 5, ' ') + ']');
                    foreach (KeyValuePair<string, int> kvp in spe.slPrintQueueEntries)
                    {
                        //Console.WriteLine ("  " + kvp.Key + " [" + CGenericMethods.FillStringLength (kvp.Value.ToString (), 5, ' ') + ']');
                        Console.WriteLine ("  " + kvp.Key + " [" + CGenericMethods.FillStringLength (kvp.Value.ToString (), 5, ' ') + "] \"" +
                                                  m_objPlotterEngine.GetHPGLString (iIdx++, 30) + '\"');
                    }
                }
                else
                {
                    Console.WriteLine (spe.strPlotName                                                                      + " ["  +
                                       CGenericMethods.FillStringLength (spe.slPrintQueueEntries.Count.ToString (), 5, ' ') + "] [" +
                                       CGenericMethods.FillStringLength (spe.iQueueSize.ToString (), 5, ' ')                + ']');
                }
            }
        }

        private void ShowSerialQueueContents (EQueueDetailLevel eQueueDetailLevel)
        {
            //Console.WriteLine ("Queue length: " + m_objPlotterEngine.GetQueueLength () + "  size: " + m_objPlotterEngine.GetQueueSize ());

            CHPGL.SPrintQueueEntry[] arPrintQueueJobList = m_objPlotterEngine.GetPrintQueueJobList ();
            int iQueueLength = m_objPlotterEngine.GetQueueLength ();
            int iQueueSize = m_objPlotterEngine.GetQueueSize ();

            if (iQueueLength == 0)
            {
                m_lstPlotEntries.Clear ();
                return;
            }

            int iQueueRemainLength = -1;
            int iQueueRemainSize = -1;
            int iIdx = -1;
            int iDeleteCount = 0;
            int iNameWidth = 0;

            try
            {
                int iDetailNameWidth = 0;
                if (eQueueDetailLevel != EQueueDetailLevel.ENoDetail)
                {

                    for (int iIdx1 = 0; iIdx1 < arPrintQueueJobList.Length; ++iIdx1)
                    {
                        CHPGL.SPrintQueueEntry pqe = arPrintQueueJobList[iIdx1];
                        if (pqe.strDocumentName.Length > iDetailNameWidth)
                        {
                            iDetailNameWidth = pqe.strDocumentName.Length;
                        }
                    }
                }

                for (iIdx = m_lstPlotEntries.Count - 1; iIdx >= 0; --iIdx)
                {
                    if (iQueueLength >= m_lstPlotEntries[iIdx].slPrintQueueEntries.Count)
                    {
                        //Console.WriteLine ("    [1] >> " + m_lstPlotEntries[iIdx].strPlotName + "   [" +
                        //                                   m_lstPlotEntries[iIdx].iQueueSize.ToString () + "]  iQueueLength: " +
                        //                                   iQueueLength.ToString ());
                        iQueueLength -= m_lstPlotEntries[iIdx].slPrintQueueEntries.Count;
                        iQueueSize -= m_lstPlotEntries[iIdx].iQueueSize;
                        iNameWidth = Math.Max (iNameWidth, m_lstPlotEntries[iIdx].strPlotName.Length);
                    }
                    else if (iQueueLength > 0)
                    {
                        iQueueRemainLength = iQueueLength;
                        iQueueRemainSize = iQueueSize;
                        iQueueLength = iQueueSize = 0;
                        //Console.WriteLine ("    [2] >> iQueueRemain: " + iQueueRemain.ToString ());
                    }
                    else if (iQueueLength == 0)
                    {
                        iDeleteCount++;
                        //Console.WriteLine ("    [3] >> iDeleteCount: " + iDeleteCount.ToString ());
                    }
                }

                for (iIdx = 0; iIdx < iDeleteCount; ++iIdx)
                {
                    if (m_lstPlotEntries.Count > 0)
                    {
                        Console.WriteLine ("    [4] >> deleting: " + m_lstPlotEntries[0].strPlotName);
                        m_lstPlotEntries.RemoveAt (0);
                    }
                }

                for (iIdx = 0; iIdx < m_lstPlotEntries.Count; ++iIdx)
                {
                    int iSpaceOffset = iDetailNameWidth - m_lstPlotEntries[iIdx].strPlotName.Length - 5;
                    if (iIdx == 0 &&
                        iQueueRemainLength > 0)
                    {
                        if (eQueueDetailLevel != EQueueDetailLevel.ENoDetail)
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].strPlotName, iNameWidth, ' ', false) + " [" +
                                               CGenericMethods.FillStringLength (iQueueRemainLength.ToString (), 3, ' ')                     +
                                               CGenericMethods.FillStringLength ("]", iSpaceOffset, ' ', false)                              + " [" +
                                               CGenericMethods.FillStringLength (iQueueRemainSize.ToString (), 5, ' ')                       + ']');
                        }
                        else
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].strPlotName, iNameWidth, ' ', false) + " [" +
                                               CGenericMethods.FillStringLength (iQueueRemainLength.ToString (), 3, ' ')                     + "] [" +
                                               CGenericMethods.FillStringLength (iQueueRemainSize.ToString (), 5, ' ')                       + ']');
                        }
                    }
                    else
                    {
                        if (eQueueDetailLevel != EQueueDetailLevel.ENoDetail)
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].strPlotName, iNameWidth, ' ', false)           + " [" +
                                               CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].slPrintQueueEntries.Count.ToString (), 3, ' ') +
                                               CGenericMethods.FillStringLength ("]", iSpaceOffset, ' ', false)                                        + " [" +
                                               CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].iQueueSize.ToString (), 5, ' ')                + ']');
                        }
                        else
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].strPlotName, iNameWidth, ' ', false)           + " [" +
                                               CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].slPrintQueueEntries.Count.ToString (), 3, ' ') + "] [" +
                                               CGenericMethods.FillStringLength (m_lstPlotEntries[iIdx].iQueueSize.ToString (), 5, ' ')                + ']');
                        }
                    }
                }

                if (eQueueDetailLevel != EQueueDetailLevel.ENoDetail)
                {
                    for (int iIdx2 = 0; iIdx2 < arPrintQueueJobList.Length; ++iIdx2)
                    {
                        CHPGL.SPrintQueueEntry pqe = arPrintQueueJobList[iIdx2];

                        if (eQueueDetailLevel == EQueueDetailLevel.EDetailNoHPGL)
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (pqe.strDocumentName, iDetailNameWidth, ' ', false) + " [" +
                                               CGenericMethods.FillStringLength (pqe.iDocumentLength.ToString (), 5, ' ')           + ']');
                        }
                        else
                        {
                            Console.WriteLine (CGenericMethods.FillStringLength (pqe.strDocumentName, iDetailNameWidth, ' ', false) + " [" +
                                               CGenericMethods.FillStringLength (pqe.iDocumentLength.ToString (), 5, ' ')           + "] \"" +
                                               m_objPlotterEngine.GetHPGLString (iIdx2, 30)                                         + '\"');
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine ("** Exception in ShowSerialQueueContents (): " + e.Message);
                if (e.InnerException != null)
                {
                    Console.WriteLine ("  " + e.InnerException.Message);
                }
                Console.WriteLine (e.StackTrace);
            }
        }

        //public struct SPrintQueueEntry  --  Spool queue entry
        //{
        //    public string strDocumentName;
        //    public int iDocumentLength;
        //}
        //class CPlotEntry  --  Internal queue master entry
        //{
        //    public string                       strPlotName          = ""; // LissajousPattern2
        //    public int                          iQueueSize           = 0;
        //    public SortedList<string, int>      slPrintQueueEntries  = new SortedList<string, int> ();  --  Detail entry list
                                                                             // LissajousPattern2_001_2021-03-30_16:09:47_001_00012
        //    public List<string>                 lstrDuplicateEntries = new List<string> ();
        //}
        private void RebuildInternalQueue ()
        {
            CHPGL.SPrintQueueEntry[] arpqePrintQueueJobList = m_objPlotterEngine.GetPrintQueueJobList ();
            List<CPlotEntry> lstNewPlotEntries = new List<CPlotEntry> ();
            string strLastPlotName = "";
            CPlotEntry objNewPlotEntry = new CPlotEntry ();

            // Iterate through arpqePrintQueueJobList
            foreach (CHPGL.SPrintQueueEntry pqe in arpqePrintQueueJobList)
            {
                string strPlotName  = ExtractPlotName (pqe.strDocumentName);
                if (strLastPlotName == "" ||
                    strLastPlotName != strPlotName)
                {
                    objNewPlotEntry = new CPlotEntry ();
                    objNewPlotEntry.strPlotName = strPlotName;
                    strLastPlotName = strPlotName;
                    lstNewPlotEntries.Add (objNewPlotEntry);
                }
                // For each entry, find the appropiate CPlotEntry element in m_lstPlotEntries,
                //   then the matching entry in objPlotEntry.slPrintQueueEntries
                //   Obtain the document length in the print queue entry and add a new SPrintQueueEntry
                //     with the name and length, and add it to a new CPlotEntry with a new slPrintQueueEntries
                for (int iIdx = 0; iIdx < m_lstPlotEntries.Count; ++ iIdx)
                {
                    CPlotEntry objOldPlotEntry = m_lstPlotEntries[iIdx];
                    if (objOldPlotEntry.strPlotName == strPlotName)
                    {
                        if (objOldPlotEntry.slPrintQueueEntries.ContainsKey (pqe.strDocumentName))
                        {
                            objNewPlotEntry.slPrintQueueEntries.Add (pqe.strDocumentName, pqe.iDocumentLength);
                            objNewPlotEntry.iQueueSize += pqe.iDocumentLength;
                        }
                        iIdx = m_lstPlotEntries.Count; // Exit loop
                    }
                }
            }

            // Now, replace the old CPlotEntry list with the new one
            m_lstPlotEntries = lstNewPlotEntries;
        }

        private string ExtractPlotName (string strDocumentName)
        {
            int iFirstUnderscoreIdx  = strDocumentName.Contains ("_") ? strDocumentName.IndexOf ('_') : 0;
            int iSecondUnderscoreIdx = iFirstUnderscoreIdx < strDocumentName.Length ? strDocumentName.IndexOf ('_', iFirstUnderscoreIdx + 1) : iFirstUnderscoreIdx;

            if (iSecondUnderscoreIdx >= 0 &&
                iSecondUnderscoreIdx >  0 &&
                iSecondUnderscoreIdx >  iFirstUnderscoreIdx)
            {
                string strPlotName = strDocumentName.Substring (iFirstUnderscoreIdx + 1, iSecondUnderscoreIdx - iFirstUnderscoreIdx - 1);
                return strPlotName;
            }

            return strDocumentName;
        }

        private void ShowOutputQueue ()
        {
            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
            {
                m_objPlotterEngine.ShowOutputQueue ();
            }
        }

        //private static void UnitTestEmbeddedComments ()
        //{
        //    const string REAL_HPGL    = "RealHPGL";
        //    const string TEST_COMMENT = "<comment>";

        //    string strTest    = CGenericMethods.PrependCommentHPGL (REAL_HPGL, TEST_COMMENT);

        //    string strComment = CGenericMethods.ExtractCommentHPGL (strTest);
        //    bool bMatch       = strComment == TEST_COMMENT;
        //    Debug.Assert (strComment == TEST_COMMENT);

        //    string strHPGL    = CGenericMethods.RemoveCommentHPGL  (strTest);
        //    bMatch            = strHPGL == REAL_HPGL;
        //    Debug.Assert (strHPGL == REAL_HPGL);
        //}

        //private void UnitTestShowQueueContents ()
        //{
        //    int iOldQueueLength = 0;
        //    m_iTestQueueEntryLength = 5;

        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-1 PlotSimpleTriangle1", iOldQueueLength, true);

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-2 PlotSimpleTriangle2", iOldQueueLength, true);
        //    m_iTestQueueEntryLength += 3;

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-3 PlotSimpleTriangle3", iOldQueueLength, true);
        //    m_iTestQueueEntryLength += 3;

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-4 PlotRadialLines", iOldQueueLength, true);
        //    m_iTestQueueEntryLength += 3;

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-5 PlotComplexTriangle", iOldQueueLength, true);
        //    m_iTestQueueEntryLength += 3;

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-6 PlotRotatedSquare", iOldQueueLength, true);
        //    m_iTestQueueEntryLength += 3;

        //    Console.WriteLine ();
        //    iOldQueueLength = m_iTestQueueLength;
        //    m_iTestQueueLength += m_iTestQueueEntryLength;
        //    InsertPlotEntry ("5-7 PlotFourQuadrants", iOldQueueLength, true);

        //    while ((m_iTestQueueLength--) > 0)
        //    {
        //        Console.WriteLine ();
        //        ShowQueueContents ();
        //    }
        //}

        private string CreateAlgebraicExpression (double[] daFactors)
        {
            StringBuilder sbExpression = new StringBuilder ();

            // { -6, 7, 3, 1 };
            // (-6 * x^3) + (7 * x^2) + (3 * x) + 1
            for (int iIdx = 0; iIdx < daFactors.Length; ++iIdx)
            {
                int iPower = daFactors.Length - iIdx - 1;
                if (iPower > 1)
                {
                    sbExpression.Append ("(" + Math.Truncate (daFactors[iIdx]).ToString () + " * x^" + iPower.ToString () + ") + ");
                }
                else if (iPower == 1)
                {
                    sbExpression.Append ("(" + Math.Truncate (daFactors[iIdx]).ToString () + " * x) + ");
                }
                else if (iPower == 0)
                {
                    sbExpression.Append (Math.Truncate (daFactors[iIdx]).ToString ());
                }
            }

            return sbExpression.ToString ();
        }

        private bool ProcessCommandLine (string[] straArgs)
        {
            foreach (string str in straArgs)
            {
                if (str == "/?")
                {
                    ShowUsage ();

                    return false; // Indicates terminating the program
                }

                string strLower = str.ToLower ();

                if (strLower.Contains ("/p"))
                {
                    if (strLower.Length > 2)
                    {
                        char cPenCount = strLower[2];
                        char cPenSequence = strLower.Length > 3 ? strLower[3] : ' ';
                        if (cPenCount >= '1' && cPenCount <= '8')
                        {
                            m_objPlotterEngine.SetPenCount ((int)cPenCount - '0');
                        }
                        else
                        {
                            ShowUsage ();
                            return false;
                        }

                        if (cPenSequence == 'r')
                        {
                            m_objPlotterEngine.SetRandomPen (true);
                            m_objPlotterEngine.SetSequentialPen (false);
                        }
                        else if (cPenSequence == 's')
                        {
                            m_objPlotterEngine.SetRandomPen (false);
                            m_objPlotterEngine.SetSequentialPen (true);
                        }
                    }
                    else
                    {
                        ShowUsage ();
                        return false;
                    }
                }
                else if (strLower.Contains ("/o"))
                {
                    if (strLower.Length <= 2)
                    {
                        ShowUsage ();
                        return false;
                    }

                    char cOutputPort = strLower[2];

                    if (cOutputPort == 's')
                    {
                        m_objPlotterEngine.SetPlotterPort ( CPlotterEngine.EPlotterPort.ESerialPort);
                    }
                    else if (cOutputPort == 'p')
                    {
                        m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.EParallelPort);
                    }
                    else if (cOutputPort == 'a')
                    {
                        m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.EAutoDetect);
                    }
                    else if (cOutputPort == 'n')
                    {
                        m_objPlotterEngine.SetPlotterPort (CPlotterEngine.EPlotterPort.ENoOutput);
                    }
                    else
                    {
                        ShowUsage ();
                        return false;
                    }
                }
                else if (strLower.Contains ("/f"))
                {
                    if (strLower.Length > 2)
                    {
                        strLower = strLower.Substring (2);
                    }
                    else
                    {
                        ShowUsage ();
                        return false;
                    }

                    if (Directory.Exists (strLower))
                    {
                        if (strLower[strLower.Length - 1] != '\\')
                        {
                            strLower += '\\';
                            m_strOutputPath = strLower;
                        }
                    }
                    else
                    {
                        ShowUsage ();
                        return false;
                    }
                }
            }

            //if (m_strOutputPath.Length == 0 &&
            //    m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ENoOutput)
            //{
            //    Console.WriteLine ("Either a plotter port or an output folder must be specified.");
            //    ShowUsage ();
            //    return false;
            //}

            return true;
        }

        private void ShowSettings ()
        {
            Console.WriteLine ("Current output port: " +
                               (m_objPlotterEngine.GetPlotterPort ()  == CPlotterEngine.EPlotterPort.EAutoDetect   ? "Auto-detect"     :
                                                          m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort   ? "Serial port:"  :
                                m_objPlotterEngine.GetPlotterPort ()  == CPlotterEngine.EPlotterPort.EParallelPort ? "Parallel port: " :
                                m_objPlotterEngine.GetPlotterPort ()  == CPlotterEngine.EPlotterPort.ENoOutput     ? "No output"       : "Not specified") +
                               ((m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort ||
                                 m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.EParallelPort) ? m_objPlotterEngine.GetPortName () : ""));

            Console.WriteLine ("Current folder: " + (m_strOutputPath.Length > 0 ? m_strOutputPath : "<none>"));

            Console.WriteLine ("Current setting: " +
                                m_objPlotterEngine.GetPenCount ().ToString () + " pens" +
                               (m_objPlotterEngine.GetSequentialPen () ? " used sequentially" :
                                                                                          m_objPlotterEngine.GetRandomPen ()     ? " selected randomly" : ""));

            Console.WriteLine ("Current sort setting: " +
                               (m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.EUnsorted               ? "not sorted"                 :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByPenOnly          ? "sort by pen only"           :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByGroupOnly        ? "sort by group only"         :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByPenAndDistance   ? "sort by pen and distance"   :
                                                           m_objPlotterEngine.GetSortMode () == CPlotterEngine.ESortMode.ESortByGroupAndDistance ? "sort by group and distance" : ""));

            if (m_objPlotterEngine.GetPlotterPort () == CPlotterEngine.EPlotterPort.ESerialPort)
            {
                Console.WriteLine (m_objPlotterEngine.GetPauseAfterNewPen () ? "Pause after pen selection" : "No pause after pen selection");
            }
        }

        private void ShowUsage ()
        {
            Console.WriteLine ("PlotterWriterConsoleUI usage:");
            Console.WriteLine ("PlotterWriterConsoleUI /p<pen selection> /o<output mode> /f<output folder>");
            Console.WriteLine ("    where <pen selection> is number of pens loaded (1 to 8) and an optional");
            Console.WriteLine ("                          character: 'r' for random pen selection or");
            Console.WriteLine ("                                     's' for sequential pen selection to");
            Console.WriteLine ("                                         indicate cycling through the pens");
            Console.WriteLine ("                                         for all selected plots");
            Console.WriteLine ("          <output mode> is one of the following:");
            Console.WriteLine ("                          S: Serial port");
            Console.WriteLine ("                          P: Parallel port");
            Console.WriteLine ("                          A: Audo-detect output port");
            Console.WriteLine ("                          N: No plotter output, file only");
            Console.WriteLine ("          <output folder> indicates writing plotter HPGL to file instead of");
            Console.WriteLine ("                          or in addition to sending HPGL to the plotter and");
            Console.WriteLine ("                          indicates the folder to which to write the files");
            Console.WriteLine ("                          example: /f/\"C:HPGL Output\"");
        }
        #endregion
    }
}
