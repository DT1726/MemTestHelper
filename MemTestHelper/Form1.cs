﻿using Microsoft.VisualBasic.Devices;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;

namespace MemTestHelper
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();

            init_cbo_threads();
            init_lst_coverage();
            init_cbo_rows();
            center_xy_offsets();

            bw_coverage = new BackgroundWorker();
            bw_coverage.WorkerSupportsCancellation = true;
            bw_coverage.DoWork += new DoWorkEventHandler(delegate (object o, DoWorkEventArgs args)
            {
                BackgroundWorker worker = o as BackgroundWorker;

                while (!worker.CancellationPending)
                {
                    update_coverage_info();
                    Thread.Sleep(UPDATE_INTERVAL);
                }

                args.Cancel = true;
            });
            bw_coverage.RunWorkerCompleted += 
            new RunWorkerCompletedEventHandler(delegate (object o, RunWorkerCompletedEventArgs args)
            {
                // wait for all MemTests to stop completely
                while (is_any_memtest_stopping())
                    Thread.Sleep(100);

                update_coverage_info(false);
            });

            timer = new System.Timers.Timer(1000);
            timer.Elapsed += new System.Timers.ElapsedEventHandler(delegate
            (object sender, System.Timers.ElapsedEventArgs e)
            {
                Invoke(new MethodInvoker(delegate
                {
                    int threads = (int)cbo_threads.SelectedItem;
                    var elapsed = e.SignalTime - start_time;

                    lbl_elapsed_time.Text = String.Format("{0:00}h{1:00}m{2:00}s",
                                                          (int)(elapsed.TotalHours),
                                                          elapsed.Minutes,
                                                          elapsed.Seconds);

                    double total_coverage = 0;
                    for (int i = 1; i <= threads; i++)
                    {
                        var info = get_coverage_info(memtest_states[i - 1].proc.MainWindowHandle);
                        if (info == null) continue;

                        total_coverage += info.Item1;
                    }

                    if (total_coverage == 0) return;

                    double diff = elapsed.TotalMilliseconds,
                           est = 0;
                    int cov = 0;
                    // use user input coverage %
                    if (chk_stop_at.Checked)
                    {
                        cov = Convert.ToInt32(txt_stop_at.Text);

                        if (chk_stop_at_total.Checked)
                            est = (diff / total_coverage * cov) - diff;
                        else
                        {
                            // calculate average coverage and use that to estimate
                            double avg = total_coverage / threads;
                            est = (diff / avg * cov) - diff;
                        }
                    }
                    else
                    {
                        // calculate average coverage and use that to estimate
                        double avg = total_coverage / threads;
                        // round up to next multiple of 100
                        cov = ((int)(avg / 100) + 1) * 100;
                        est = (diff / avg * cov) - diff;
                    }

                    TimeSpan est_time = TimeSpan.FromMilliseconds(est);
                    lbl_estimated_time.Text = String.Format("{0:00}h{1:00}m{2:00}s to {3}%",
                                                            (int)(est_time.TotalHours),
                                                            est_time.Minutes,
                                                            est_time.Seconds,
                                                            cov);

                    int ram = Convert.ToInt32(txt_ram.Text);
                    double speed = (total_coverage / 100) * ram / (diff / 1000);
                    lbl_speed_value.Text = $"{speed:f2}MB/s";
                }));
            });
        }

        // event handling

        private void Form1_Load(object sender, EventArgs e)
        {
            load_cfg();
            update_form_height();
            update_lst_coverage_items();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            close_memtests();
            save_cfg();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            int threads = (int)cbo_threads.SelectedItem;
            switch (WindowState)
            {
                // minimise MemTest instances
                case FormWindowState.Minimized:
                    run_in_background(new MethodInvoker(delegate
                    {
                        for (int i = 0; i < threads; i++)
                        {
                            if (memtest_states[i] != null)
                            {
                                IntPtr hwnd = memtest_states[i].proc.MainWindowHandle;

                                if (!IsIconic(hwnd))
                                    ShowWindow(hwnd, SW_MINIMIZE);

                                Thread.Sleep(10);
                            }
                        }
                    }));
                    break;

                // restore previous state of MemTest instances
                case FormWindowState.Normal:
                    run_in_background(new MethodInvoker(delegate
                    {
                        /*
                         * is_minimised is true when user clicked the hide button
                         * this means that the memtest instances should be kept minimised
                         */ 
                        if (!is_minimised)
                        {
                            for (int i = 0; i < threads; i++)
                            {
                                if (memtest_states[i] != null)
                                {
                                    IntPtr hwnd = memtest_states[i].proc.MainWindowHandle;

                                    ShowWindow(hwnd, SW_RESTORE);

                                    Thread.Sleep(10);
                                }
                            }

                            // user may have changed offsets while minimised
                            move_memtests();

                            // hack to bring form to top
                            TopMost = true;
                            Thread.Sleep(10);
                            TopMost = false;
                        }
                    }));
                    break;
            }

            // update the height
            if (Size.Height >= MinimumSize.Height && Size.Height <= MaximumSize.Height)
                ud_win_height.Value = Size.Height;
        }

        private void btn_auto_ram_Click(object sender, EventArgs e)
        {
            txt_ram.Text = get_free_ram().ToString();
        }

        private void btn_run_Click(object sender, EventArgs e)
        {
            if (!File.Exists(MEMTEST_EXE))
            {
                MessageBox.Show(MEMTEST_EXE + " not found");
                return;
            }

            if (!validate_input()) return;

            btn_auto_ram.Enabled = false;
            txt_ram.Enabled = false;
            cbo_threads.Enabled = false;
            //cbo_rows.Enabled = false;
            btn_run.Enabled = false;
            btn_stop.Enabled = true;
            chk_stop_at.Enabled = false;
            txt_stop_at.Enabled = false;
            chk_stop_at_total.Enabled = false;
            chk_stop_on_err.Enabled = false;
            chk_start_min.Enabled = false;

            // run in background as start_memtests can block
            run_in_background(new MethodInvoker(delegate
            {
                start_memtests();

                if (!bw_coverage.IsBusy)
                    bw_coverage.RunWorkerAsync();
                start_time = DateTime.Now;
                timer.Start();

                Activate();
            }));
        }

        private void btn_stop_Click(object sender, EventArgs e)
        {
            Parallel.For(0, (int)cbo_threads.SelectedItem, i =>
            {
                if (!memtest_states[i].is_finished)
                    ControlClick(memtest_states[i].proc.MainWindowHandle, MEMTEST_BTN_STOP); 
            });

            bw_coverage.CancelAsync();
            timer.Stop();

            btn_auto_ram.Enabled = true;
            txt_ram.Enabled = true;
            cbo_threads.Enabled = true;
            //cbo_rows.Enabled = true;
            btn_run.Enabled = true;
            btn_stop.Enabled = false;
            chk_stop_at.Enabled = true;
            if (chk_stop_at.Checked)
            {
                txt_stop_at.Enabled = true;
                chk_stop_at_total.Enabled = true;
            }
            chk_stop_on_err.Enabled = true;
            chk_start_min.Enabled = true;

            // wait for all memtests to fully stop
            while (is_any_memtest_stopping())
                Thread.Sleep(100);

            MessageBox.Show("MemTest finished");
        }

        private void btn_show_Click(object sender, EventArgs e)
        {
            // run in background as Thread.Sleep can lockup the GUI
            int threads = (int)cbo_threads.SelectedItem;
            run_in_background(new MethodInvoker(delegate
            {
                for (int i = 0; i < threads; i++)
                {
                    if (memtest_states[i] != null)
                    {
                        IntPtr hwnd = memtest_states[i].proc.MainWindowHandle;

                        if (IsIconic(hwnd))
                            ShowWindow(hwnd, SW_RESTORE);
                        else
                            SetForegroundWindow(hwnd);

                        Thread.Sleep(10);
                    }
                }

                is_minimised = false;

                // user may have changed offsets while minimised
                move_memtests();

                Activate();
            }));
        }

        private void btn_hide_Click(object sender, EventArgs e)
        {
            int threads = (int)cbo_threads.SelectedItem;
            run_in_background(new MethodInvoker(delegate
            {
                for (int i = 0; i < threads; i++)
                {
                    if (memtest_states[i] != null)
                    {
                        IntPtr hwnd = memtest_states[i].proc.MainWindowHandle;

                        if (!IsIconic(hwnd))
                            ShowWindow(hwnd, SW_MINIMIZE);

                        Thread.Sleep(10);
                    }
                }

                is_minimised = true;
            }));
        }

        private void offset_changed(object sender, EventArgs e)
        {
            run_in_background(new MethodInvoker(delegate { move_memtests(); }));
        }

        private void btn_center_Click(object sender, EventArgs e)
        {
            center_xy_offsets();
        }

        private void cbo_rows_SelectionChangeCommitted(object sender, EventArgs e)
        {
            center_xy_offsets();
        }

        private void cbo_threads_SelectionChangeCommitted(object sender, EventArgs e)
        {
            update_lst_coverage_items();

            cbo_rows.Items.Clear();
            init_cbo_rows();
            center_xy_offsets();
        }

        private void chk_stop_at_CheckedChanged(object sender, EventArgs e)
        {
            if (chk_stop_at.Checked)
            {
                txt_stop_at.Enabled = true;
                chk_stop_at_total.Enabled = true;
            }
            else
            {
                txt_stop_at.Enabled = false;
                chk_stop_at_total.Enabled = false;
            }
        }

        private void ud_win_height_ValueChanged(object sender, EventArgs e)
        {
            update_form_height();
        }

        // helper functions

        // returns free RAM in MB
        private UInt64 get_free_ram()
        {
            /*
             * Available RAM = Free + Standby
             * https://superuser.com/a/1032481
             * 
             * Cached = sum of stuff
             * https://www.reddit.com/r/PowerShell/comments/ao59ha/cached_memory_as_it_appears_in_the_performance/efye75r/
             * 
             * Standby = Cached - Modifed
             */
            UInt64 avail = new ComputerInfo().AvailablePhysicalMemory;
            float standby = new PerformanceCounter("Memory", "Cache Bytes").NextValue() +
                            //new PerformanceCounter("Memory", "Modified Page List Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Core Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes").NextValue() +
                            new PerformanceCounter("Memory", "Standby Cache Reserve Bytes").NextValue();

            return (UInt64)((avail - standby) / (1024 * 1024));
        }

        // TODO: error checking
        private bool load_cfg()
        {
            string[] valid_keys = { "ram", "threads", "x_offset", "y_offset",
                                    "x_spacing", "y_spacing", "rows", "stop_at",
                                    "stop_at_value", "stop_at_total", "stop_on_error",
                                    "start_min", "win_height" };

            try
            {
                string[] lines = File.ReadAllLines(CFG_FILENAME);
                Dictionary<string, int> cfg = new Dictionary<string, int>();

                foreach (string l in lines)
                {
                    string[] s = l.Split('=');
                    if (s.Length != 2) continue;
                    s[0] = s[0].Trim();
                    s[1] = s[1].Trim();

                    if (valid_keys.Contains(s[0]))
                    {
                        if (s[1].Length == 0) continue;

                        int v;
                        if (Int32.TryParse(s[1], out v))
                            cfg.Add(s[0], v);
                        else return false;
                    }
                    else return false;
                }

                foreach (KeyValuePair<string, int> kv in cfg)
                {
                    switch (kv.Key)
                    {
                        case "ram":
                            txt_ram.Text = kv.Value.ToString();
                            break;
                        case "threads":
                            cbo_threads.SelectedItem = kv.Value;
                            break;

                        case "x_offset":
                            ud_x_offset.Value = kv.Value;
                            break;
                        case "y_offset":
                            ud_y_offset.Value = kv.Value;
                            break;

                        case "x_spacing":
                            ud_x_spacing.Value = kv.Value;
                            break;
                        case "y_spacing":
                            ud_y_spacing.Value = kv.Value;
                            break;

                        case "stop_at":
                            chk_stop_at.Checked = kv.Value != 0;
                            break;
                        case "stop_at_value":
                            txt_stop_at.Text = kv.Value.ToString();
                            break;
                        case "stop_at_total":
                            chk_stop_at_total.Checked = kv.Value != 0;
                            break;

                        case "stop_on_error":
                            chk_stop_on_err.Checked = kv.Value != 0;
                            break;

                        case "start_min":
                            chk_start_min.Checked = kv.Value != 0;
                            break;

                        case "win_height":
                            ud_win_height.Value = kv.Value;
                            break;
                    }
                }
            }
            catch(FileNotFoundException e)
            {
                return false;
            }

            return true;
        }

        private bool save_cfg()
        {
            try {
                var file = new StreamWriter(CFG_FILENAME);
                List<string> lines = new List<string>();

                lines.Add($"ram = {txt_ram.Text}");
                lines.Add($"threads = {(int)cbo_threads.SelectedItem}");

                lines.Add($"x_offset = {ud_x_offset.Value}");
                lines.Add($"y_offset = {ud_y_offset.Value}");
                lines.Add($"x_spacing = {ud_x_spacing.Value}");
                lines.Add($"y_spacing = {ud_y_spacing.Value}");
                lines.Add($"rows = {cbo_rows.SelectedItem}");

                lines.Add(string.Format("stop_at = {0}", chk_stop_at.Checked ? 1 : 0));
                lines.Add($"stop_at_value = {txt_stop_at.Text}");
                lines.Add(string.Format("stop_at_total = {0}", chk_stop_at_total.Checked ? 1 : 0));
                lines.Add(string.Format("stop_on_error = {0}", chk_stop_on_err.Checked ? 1 : 0));

                lines.Add(string.Format("start_min = {0}", chk_start_min.Checked ? 1 : 0));

                lines.Add($"win_height = {ud_win_height.Value}");

                foreach (string l in lines)
                    file.WriteLine(l);

                file.Close();
            }
            catch (Exception e)
            {
                return false;
            }

            return true;
        }

        private void update_form_height()
        {
            Size = new Size(Size.Width, (int)ud_win_height.Value);
        }

        private bool validate_input()
        {
            ComputerInfo ci = new ComputerInfo();
            UInt64 total_ram = ci.TotalPhysicalMemory / (1024 * 1024),
                   avail_ram = ci.AvailablePhysicalMemory / (1024 * 1024);

            string str_ram = txt_ram.Text;
            // automatically input available ram if empty
            if (str_ram.Length == 0)
            {
                str_ram = get_free_ram().ToString();
                txt_ram.Text = str_ram;
            }
            else
            {
                if (!str_ram.All(char.IsDigit))
                {
                    show_error_msgbox("Amount of RAM must be an integer");
                    return false;
                }
            }

            int threads = (int)cbo_threads.SelectedItem,
                ram = Convert.ToInt32(str_ram);
            if (ram < threads)
            {
                show_error_msgbox($"Amount of RAM must be greater than {threads}");
                return false;
            }

            if (ram > MEMTEST_MAX_RAM * threads)
            {
                show_error_msgbox(
                    $"Amount of RAM must be at most {MEMTEST_MAX_RAM * threads}\n" + 
                    "Try increasing the number of threads\n" + 
                    "or reducing amount of RAM"
                );
                return false;
            }

            if ((UInt64)ram > total_ram)
            {
                show_error_msgbox($"Amount of RAM exceeds total RAM ({total_ram})");
                return false;
            }

            if ((UInt64)ram > avail_ram)
            {
                var res = MessageBox.Show(
                    $"Amount of RAM exceeds available RAM ({avail_ram})\n" +
                    "This will cause RAM to be paged to your storage,\n" +
                    "which may make MemTest really slow.\n" +
                    "Continue?",
                    "Warning",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning
                );
                if (res == DialogResult.No)
                    return false;
            }

            // validate stop at % and error count
            if (chk_stop_at.Checked)
            {
                string str_stop_at = txt_stop_at.Text;

                if (str_stop_at == "")
                {
                    show_error_msgbox("Please enter stop at (%)");
                    return false;
                }

                if (!str_stop_at.All(char.IsDigit))
                {
                    show_error_msgbox("Stop at (%) must be an integer");
                    return false;
                }

                int stop_at = Convert.ToInt32(str_stop_at);
                if (stop_at <= 0)
                {
                    show_error_msgbox("Stop at (%) must be greater than 0");
                    return false;
                }
            }

            return true;
        }

        private void init_lst_coverage()
        {
            for (int i = 0; i <= (int)cbo_threads.SelectedItem; i++)
            {
                string[] row = { i.ToString(), "-", "-" };
                // first row is total
                if (i == 0) row[0] = "T";

                lst_coverage.Items.Add(new ListViewItem(row));
            }
        }

        private void update_lst_coverage_items()
        {
            int threads = (int)cbo_threads.SelectedItem;
            var items = lst_coverage.Items;
            if (threads < items.Count)
            {
                for (int i = items.Count - 1; i > threads; i--)
                    items.RemoveAt(i);
            }
            else
            {
                for (int i = items.Count; i <= threads; i++)
                {
                    string[] row = { i.ToString(), "-", "-" };
                    lst_coverage.Items.Add(new ListViewItem(row));
                }
            }
        }

        private void init_cbo_threads()
        {
            for (int i = 0; i < MAX_THREADS; i++)
                cbo_threads.Items.Add(i + 1);

            cbo_threads.SelectedItem = NUM_THREADS;
        }

        private void init_cbo_rows()
        {
            int threads = (int)cbo_threads.SelectedItem;

            for (int i = 1; i <= threads; i++)
            {
                if (threads % i == 0)
                    cbo_rows.Items.Add(i);
            }

            cbo_rows.SelectedItem = threads % 2 == 0 ? 2 : 1;
        }

        void center_xy_offsets()
        {
            Rectangle screen = Screen.FromControl(this).Bounds;
            int rows = (int)cbo_rows.SelectedItem,
                cols = (int)cbo_threads.SelectedItem / rows,
                x_offset = (screen.Width - MEMTEST_WIDTH * cols) / 2,
                y_offset = (screen.Height - MEMTEST_HEIGHT * rows) / 2;

            ud_x_offset.Value = x_offset;
            ud_y_offset.Value = y_offset;
        }

        private void start_memtests()
        {
            close_all_memtests();

            int threads = (int)cbo_threads.SelectedItem;
            Parallel.For(0, threads, i => 
            {
                MemTestState state = new MemTestState();
                state.proc = Process.Start(MEMTEST_EXE);
                state.is_finished = false;
                memtest_states[i] = state;

                // wait for process to start
                while (string.IsNullOrEmpty(state.proc.MainWindowTitle))
                {
                    Thread.Sleep(100);
                    state.proc.Refresh();
                }

                IntPtr hwnd = state.proc.MainWindowHandle;
                double ram = Convert.ToDouble(txt_ram.Text) / threads;

                ControlSetText(hwnd, MEMTEST_EDT_RAM, $"{ram:f2}");
                ControlSetText(hwnd, MEMTEST_STATIC_FREE_VER, "Modified version by ∫ntegral#7834");
                ControlClick(hwnd, MEMTEST_BTN_START);

                if (chk_start_min.Checked)
                    ShowWindow(hwnd, SW_MINIMIZE);
            });

            if (!chk_start_min.Checked)
                move_memtests();
        }

        private void move_memtests()
        {
            int x_offset = (int)ud_x_offset.Value,
                y_offset = (int)ud_y_offset.Value,
                x_spacing = (int)ud_x_spacing.Value - 5,
                y_spacing = (int)ud_y_spacing.Value - 3,
                rows = (int)cbo_rows.SelectedItem,
                cols = (int)cbo_threads.SelectedItem / rows;

            Parallel.For(0, (int)cbo_threads.SelectedItem, i =>
            {
                 MemTestState state = memtest_states[i];
                 if (state == null) return;

                 IntPtr hwnd = state.proc.MainWindowHandle;
                 int r = i / cols,
                     c = i % cols,
                     x = c * MEMTEST_WIDTH + c * x_spacing + x_offset,
                     y = r * MEMTEST_HEIGHT + r * y_spacing + y_offset;

                 MoveWindow(hwnd, x, y, MEMTEST_WIDTH, MEMTEST_HEIGHT, true);
            });
        }

        // only close MemTests started by MemTestHelper
        private void close_memtests()
        {
            Parallel.ForEach(memtest_states, s =>
            {
                try {
                    if (s != null) s.proc.Kill();
                }
                catch (Exception) { }
            });
        }

        /* 
         * close all MemTests, regardless of if they were
         * started by MemTestHelper
         */
        private void close_all_memtests()
        {
            // remove the .exe
            string name = MEMTEST_EXE.Substring(0, MEMTEST_EXE.Length - 4);
            var procs = Process.GetProcessesByName(name);
            Parallel.ForEach(procs, p => { p.Kill(); });
        }

        // returns (coverage, errors)
        private Tuple<double, int> get_coverage_info(IntPtr hwnd)
        {
            string str = ControlGetText(hwnd, MEMTEST_STATIC_COVERAGE);
            if (str == "" || !str.Contains("Coverage")) return null;

            // Test over. 47.3% Coverage, 0 Errors
            //            ^^^^^^^^^^^^^^^^^^^^^^^^
            int start = str.IndexOfAny("0123456789".ToCharArray());
            if (start == -1) return null;
            str = str.Substring(start);

            // 47.3% Coverage, 0 Errors
            // ^^^^
            // some countries use a comma as the decimal point
            string coverage_str = str.Split("%".ToCharArray())[0].Replace(',', '.');
            double coverage = 0;
            double.TryParse(coverage_str, NumberStyles.Any, CultureInfo.InvariantCulture, out coverage);

            // 47.3% Coverage, 0 Errors
            //                 ^^^^^^^^
            start = str.IndexOf("Coverage, ") + "Coverage, ".Length;
            str = str.Substring(start);
            // 0 Errors
            // ^
            int errors = Convert.ToInt32(str.Substring(0, str.IndexOf(" Errors")));

            return Tuple.Create(coverage, errors);
        }

        private void update_coverage_info(bool should_check = true)
        {
            lst_coverage.Invoke(new MethodInvoker(delegate
            {
                int threads = (int)cbo_threads.SelectedItem;
                double total_coverage = 0;
                int total_errors = 0;

                // total is index 0
                for (int i = 1; i <= threads; i++)
                {
                    var hwnd = memtest_states[i - 1].proc.MainWindowHandle;
                    var info = get_coverage_info(hwnd);
                    if (info == null) continue;
                    double coverage = info.Item1;
                    int errors = info.Item2;

                    lst_coverage.Items[i].SubItems[1].Text = string.Format("{0:f1}", coverage);
                    lst_coverage.Items[i].SubItems[2].Text = errors.ToString();
                        
                    if (should_check)
                    {
                        // check coverage %
                        if (chk_stop_at.Checked && !chk_stop_at_total.Checked)
                        {
                            int stop_at = Convert.ToInt32(txt_stop_at.Text);
                            if (coverage > stop_at)
                            {
                                if (!memtest_states[i - 1].is_finished)
                                {
                                    ControlClick(memtest_states[i - 1].proc.MainWindowHandle,
                                                 MEMTEST_BTN_STOP);
                                    memtest_states[i - 1].is_finished = true;
                                }
                            }
                        }

                        // check error count
                        if (chk_stop_on_err.Checked)
                        {
                            if (errors > 0)
                            {
                                lst_coverage.Items[i].SubItems[1].ForeColor = Color.Red;

                                click_btn_stop();
                            }
                        }
                    }

                    total_coverage += coverage;
                    total_errors += errors;
                }

                // update the total coverage and errors
                lst_coverage.Items[0].SubItems[1].Text = string.Format("{0:f1}", total_coverage);
                lst_coverage.Items[0].SubItems[2].Text = total_errors.ToString();

                if (should_check)
                {
                    // check total coverage
                    if (chk_stop_at.Checked && chk_stop_at_total.Checked)
                    {
                        int stop_at = Convert.ToInt32(txt_stop_at.Text);
                        if (total_coverage > stop_at)
                            click_btn_stop();
                    }

                    if (is_all_finished())
                        click_btn_stop();
                }
            }));
        }

        /*
         * MemTest can take a while to stop,
         * which causes the total to return 0
         */
        private bool is_any_memtest_stopping()
        {
            for (int i = 0; i < (int)cbo_threads.SelectedItem; i++)
            {
                IntPtr hwnd = memtest_states[i].proc.MainWindowHandle;
                string str = ControlGetText(hwnd, MEMTEST_STATIC_COVERAGE);
                if (str != "" && str.Contains("Ending")) return true;
            }

            return false;
        }

        /* 
         * PerformClick() only works if the button is visible
         * switch to main tab and PerformClick() then switch
         * back to the tab that the user was on
         */
        private void click_btn_stop()
        {
            var curr_tab = tab_control.SelectedTab;
            if (curr_tab != tab_main)
                tab_control.SelectedTab = tab_main;

            btn_stop.PerformClick();
            tab_control.SelectedTab = curr_tab;
        }

        private void show_error_msgbox(string msg)
        {
            MessageBox.Show(
                msg,
                "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        private bool is_all_finished()
        {
            for (int i = 0; i < (int)cbo_threads.SelectedItem; i++)
            {
                if (!memtest_states[i].is_finished)
                    return false;
            }

            return true;
        }

        private void run_in_background(Delegate method)
        {
            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += new DoWorkEventHandler(delegate (object s, DoWorkEventArgs args)
            {
                Invoke(method);
            });
            bw.RunWorkerAsync();
        }

        /*
         * class_name should be <classname><n>
         * tries to split class_name as above
         * returns (<classname>, <n>) if possible
         * otherwise, returns null
         */
        private Tuple<string, int> split_class_name(string class_name)
        {
            Regex regex = new Regex(@"([a-zA-Z]+)(\d+)");
            Match match = regex.Match(class_name);

            if (!match.Success) return null;

            return Tuple.Create(
                match.Groups[1].Value,
                Convert.ToInt32(match.Groups[2].Value)
            );
        }

        /*
         * class_name should be <classname><n>
         * where <classname> is the name of the class to find
         *       <n>         is the nth window with that matches <classname> (1 indexed)
         * e.g. Edit1
         * returns the handle to the window if found
         * otherwise, returns IntPtr.Zero
         */
        private IntPtr find_window(IntPtr hwnd_parent, string class_name)
        {
            if (hwnd_parent == IntPtr.Zero)
                return IntPtr.Zero;

            var name = split_class_name(class_name);
            if (name == null) return IntPtr.Zero;

            IntPtr hwnd = IntPtr.Zero;
            for (int i = 0; i < name.Item2; i++)
                hwnd = FindWindowEx(hwnd_parent, hwnd, name.Item1, null);

            return hwnd;
        }

        // emulate AutoIT Control functions
        private bool ControlClick(IntPtr hwnd_parent, string class_name)
        {
            IntPtr hwnd = find_window(hwnd_parent, class_name);
            if (hwnd == IntPtr.Zero) return false;
            SendNotifyMessage(hwnd, BM_CLICK, IntPtr.Zero, null);
            return true;
        }

        private bool ControlSetText(IntPtr hwnd_parent, string class_name, string text)
        {
            IntPtr hwnd = find_window(hwnd_parent, class_name);
            if (hwnd == IntPtr.Zero) return false;
            return SendMessage(hwnd, WM_SETTEXT, IntPtr.Zero, text) != IntPtr.Zero;
        }

        private string ControlGetText(IntPtr hwnd, string class_name)
        {
            IntPtr hwnd_control = find_window(hwnd, class_name);
            if (hwnd_control == IntPtr.Zero) return null;
            int len = GetWindowTextLength(hwnd_control);
            StringBuilder str = new StringBuilder(len + 1);
            GetWindowText(hwnd_control, str, str.Capacity);
            return str.ToString();
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        // blocks
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        // doesn't block
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool SendNotifyMessage(IntPtr hWnd, int Msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // is minimised
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsIconic(IntPtr hWnd);

        public const int WM_SETTEXT = 0xC, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
                         SW_SHOW = 5, SW_RESTORE = 9, SW_MINIMIZE = 6, BM_CLICK = 0xF5;

        private static int NUM_THREADS = Convert.ToInt32(System.Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS")),
                           MAX_THREADS = NUM_THREADS * 4,
                           UPDATE_INTERVAL = 200;   // interval (in ms) for coverage info list

        private const string MEMTEST_EXE = "memtest_6.0_no_nag.exe",
                             MEMTEST_BTN_START = "Button1",
                             MEMTEST_BTN_STOP = "Button2",
                             MEMTEST_EDT_RAM = "Edit1",
                             MEMTEST_STATIC_COVERAGE = "Static1",
                             // If you find this free version useful...
                             MEMTEST_STATIC_FREE_VER = "Static2",
                             CFG_FILENAME = "MemTestHelper.cfg";

        private const int MEMTEST_WIDTH = 217,
                          MEMTEST_HEIGHT = 247,
                          MEMTEST_MAX_RAM = 2048;

        private MemTestState[] memtest_states = new MemTestState[MAX_THREADS];
        private BackgroundWorker bw_coverage;
        private DateTime start_time;
        private System.Timers.Timer timer;
        private bool is_minimised = true;

        class MemTestState
        {
            public Process proc;
            public bool is_finished;
        }
    }
}
