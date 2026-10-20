// inFamous Launcher entry point.
//   inFamous Launcher.exe                 opens the launcher
//   inFamous Launcher.exe --play          starts the game with the saved settings, no window
//   inFamous Launcher.exe --dry-run FILE  writes what --play would run (exe, arguments, environment) to FILE
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace InfamousLauncher
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--dry-run") return DryRun(args[1]);
            if (args.Length >= 1 && args[0] == "--play") return PlayDirect();

            bool created;
            using (var mutex = new Mutex(true, @"Local\inFamousLauncher", out created))
            {
                if (!created) { FocusExisting(); return 0; }
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                using (var s = typeof(Program).Assembly.GetManifestResourceStream("InfamousLauncher.Theme.xaml"))
                    app.Resources = (ResourceDictionary)XamlReader.Load(s);
                app.DispatcherUnhandledException += (o, e) =>
                {
                    MessageBox.Show("Something went wrong:\n\n" + e.Exception.Message + "\n\n" + e.Exception.StackTrace, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                SystemInfo.Probe();
                var model = new Model(GamePaths.Find());
                model.Load();
                return app.Run(new MainWindow(model));
            }
        }

        static void FocusExisting()
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
                if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero)
                {
                    Native.ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                    Native.SetForegroundWindow(p.MainWindowHandle);
                }
        }

        static Model LoadModel(out string error)
        {
            var m = new Model(GamePaths.Find());
            m.Load();
            error = m.SpecError;
            if (error == null && !File.Exists(m.ResolveExe())) error = "The game build is missing: " + m.ResolveExe();
            return m;
        }

        static int PlayDirect()
        {
            string error;
            var m = LoadModel(out error);
            if (error != null) { MessageBox.Show(error, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return 1; }
            try
            {
                m.WriteSettingsIni();
                string nr;
                try { nr = Dlss5.Sync(m); }
                catch (Exception e) { nr = "DLSS 5 Neural Rendering could not be set up: " + e.Message + "\n\nThe game starts without it."; }
                if (nr != null) MessageBox.Show(nr, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Warning);
                using (RunningGame.Start(m.ResolveExe(), m.Spec.Args, m.Spec.Cwd, m.BuildEnvironment(), m.StdoutLog, m.StderrLog, m.Cfg.Priority)) { }
                m.Cfg.LastPlayed = DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv.C);
                m.Cfg.Save(m.P.LauncherIni);
                return 0;
            }
            catch (Exception e) { MessageBox.Show("The game could not be started.\n\n" + e.Message, "inFamous Launcher", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
        }

        static int DryRun(string outFile)
        {
            string error;
            var m = LoadModel(out error);
            var sb = new StringBuilder();
            sb.AppendLine("root=" + m.P.Root);
            if (error != null) sb.AppendLine("error=" + error);
            if (m.Spec != null)
            {
                sb.AppendLine("exe=" + m.ResolveExe());
                sb.AppendLine("cwd=" + m.Spec.Cwd);
                foreach (var a in m.Spec.Args) sb.AppendLine("arg=" + a);
                sb.AppendLine("stdout=" + m.StdoutLog);
                sb.AppendLine("stderr=" + m.StderrLog);
                sb.AppendLine("priority=" + m.Cfg.Priority);
                sb.AppendLine("dlss5=" + Dlss5.Status(m));
                sb.AppendLine("settings.ini=" + m.SettingsIni);
                foreach (var k in Catalog.IniKeys) sb.AppendLine("ini " + k + "=" + m.Get(Catalog.Find(k)));
                foreach (var mod in m.ModList) sb.AppendLine("mod " + mod.Id + " available=" + mod.Available + " enabled=" + m.ModEnabled(mod) + " env=" + string.Join(",", mod.Env.Select(kv => kv.Key + "=" + kv.Value)));
                // Only what the game gets on top of the launcher's own environment.
                var mine = Environment.GetEnvironmentVariables();
                var env = m.BuildEnvironment();
                foreach (var k in env.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    if (!mine.Contains(k) || (string)mine[k] != env[k]) sb.AppendLine("env " + k + "=" + env[k]);
                foreach (System.Collections.DictionaryEntry e in mine)
                    if (!env.ContainsKey((string)e.Key)) sb.AppendLine("unset " + e.Key);
            }
            File.WriteAllText(outFile, sb.ToString());
            return error == null ? 0 : 1;
        }
    }
}
