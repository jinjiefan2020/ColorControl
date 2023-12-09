﻿using ColorControl.Forms;
using ColorControl.Services.Common;
using ColorControl.Services.EventDispatcher;
using ColorControl.Services.GameLauncher;
using ColorControl.Services.LG;
using ColorControl.Services.NVIDIA;
using ColorControl.Services.Samsung;
using ColorControl.Shared.Common;
using ColorControl.Shared.Contracts;
using ColorControl.Shared.Native;
using ColorControl.Shared.Services;
using ColorControl.Svc;
using ColorControl.XForms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NLog;
using NLog.Config;
using NWin32;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ColorControl
{
    static class Program
    {
        public const string TS_TASKNAME = "ColorControl";
        public static string DataDir { get; private set; }
        public static string ConfigFilename { get; private set; }
        public static string LogFilename { get; private set; }
        public static Config Config { get; private set; }
        public static GlobalContext AppContext { get; private set; }

        public static bool IsRestarting { get; private set; }
        public static bool UserExit = false;

        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static Mutex _mutex;
        private static MainForm _mainForm;
        private static LoggingRule _loggingRule;

        [STAThread] // autogenerated
        public static void Main(string[] args)
        {
            // STA
            MainAsync(args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        //[STAThread]
        static async Task MainAsync(string[] args)
        {
            DataDir = Utils.GetDataPath();

            var runAsService = args.Contains("--service") || WinApiService.IsAdministratorStatic() && Process.GetCurrentProcess().Parent()?.ProcessName?.Equals("services", StringComparison.InvariantCultureIgnoreCase) == true;
            var runElevated = args.Contains("--elevated");

            InitLogger(runAsService, runElevated);

            Logger.Debug($"Using data path: {DataDir}");
            //Logger.Debug("Parent process: " + Process.GetCurrentProcess().Parent()?.ProcessName);

            if (runAsService)
            {
                await RunService(args);
                return;
            }

            var host = CreateHostBuilder().Build();
            ServiceProvider = host.Services;

            var mutexId = $"Global\\{typeof(MainForm).GUID}";

            var currentDomain = AppDomain.CurrentDomain;
            // Handler for unhandled exceptions.
            currentDomain.UnhandledException += GlobalUnhandledExceptionHandler;
            // Handler for exceptions in threads behind forms.
            Application.ThreadException += GlobalThreadExceptionHandler;

            LoadConfig();

            var appContextProvider = ServiceProvider.GetRequiredService<AppContextProvider>();
            var startUpParams = StartUpParams.Parse(args);

            AppContext = new GlobalContext(Config, startUpParams, DataDir, _loggingRule, mutexId);
            appContextProvider.SetAppContext(AppContext);

            if (Config.UseGdiScaling)
            {
                Application.SetHighDpiMode(HighDpiMode.DpiUnawareGdiScaled);
            }

            var existingProcess = Utils.GetProcessByName("ColorControl");

            try
            {
                if (await CommandLineHandler.HandleStartupParams(startUpParams, existingProcess))
                {
                    return;
                }
            }
            finally
            {
                Utils.CloseConsole();
            }

            _mutex = new Mutex(true, AppContext.MutexId, out var mutexCreated);
            try
            {
                if (!mutexCreated)
                {
                    if (existingProcess != null && existingProcess.Threads.Count > 0)
                    {
                        var thread = existingProcess.Threads[0];
                        NativeMethods.EnumThreadWindows((uint)thread.Id, EnumThreadWindows, IntPtr.Zero);

                        return;
                    }

                    MessageBox.Show("Only one instance of this program can be active.", "ColorControl");
                }
                else
                {
                    _mutex.WaitOne();
                    try
                    {
                        //if (Debugger.IsAttached)
                        //{
                        //    Utils.StartService();
                        //}

                        //var result = WinApi.SetPreferredAppMode(1);
                        //result = WinApi.RefreshImmersiveColorPolicyState();

                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);

                        if (Debugger.IsAttached)
                        {
                            var winApiService = ServiceProvider.GetRequiredService<WinApiService>();

                            if (winApiService.IsAdministrator())
                            {
                                var backgroundService = ServiceProvider.GetRequiredService<ColorControlBackgroundService>();
                                backgroundService.PipeName = PipeUtils.ElevatedPipe;

                                Task.Run(async () => await backgroundService.StartAsync(CancellationToken.None));
                            }
                        }

                        _mainForm = ServiceProvider.GetRequiredService<MainForm>();
                        Application.Run(_mainForm);

                        if (Debugger.IsAttached && !IsRestarting)
                        {
                            var winApiAdminService = ServiceProvider.GetRequiredService<WinApiAdminService>();
                            winApiAdminService.StopService();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error while initializing application: " + ex.ToLogString(Environment.StackTrace), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            finally
            {
                if (mutexCreated)
                {
                    _mutex?.Close();
                }
            }
        }

        public static void Restart()
        {
            IsRestarting = true;

            _mainForm?.CloseForRestart();

            _mutex?.Close();
            _mutex = null;

            Application.Restart();
            Environment.Exit(0);
        }

        private static void InitLogger(bool runAsService, bool runElevated)
        {
            var config = new LoggingConfiguration();

            // Targets where to log to: File and Console
            var logFileAppendix = runAsService ? "_svc" : runElevated ? "_elevated" : "";

            LogFilename = Path.Combine(DataDir, $"LogFile{logFileAppendix}.txt");

            var logfile = new NLog.Targets.FileTarget("logfile") { FileName = LogFilename };

            _loggingRule = new LoggingRule("*", LogLevel.Trace, LogLevel.Fatal, logfile);
            _loggingRule.RuleName = "ColorControl";

            // Rules for mapping loggers to targets            
            config.AddRule(_loggingRule);

            // Apply config           
            LogManager.Configuration = config;
        }

        private static void LoadConfig()
        {
            ConfigFilename = Path.Combine(DataDir, "Settings.json");

            try
            {
                if (File.Exists(ConfigFilename))
                {
                    var data = File.ReadAllText(ConfigFilename);
                    Config = JsonConvert.DeserializeObject<Config>(data);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"LoadConfig: {ex.Message}");
            }
            Config ??= new Config();

            var minLogLevel = LogLevel.FromString(Config.LogLevel);

            if (minLogLevel != LogLevel.Trace)
            {
                _loggingRule.SetLoggingLevels(minLogLevel, LogLevel.Fatal);
                LogManager.ReconfigExistingLoggers();
            }
        }

        public static IServiceProvider ServiceProvider { get; private set; }
        private static IHostBuilder CreateHostBuilder()
        {
            return Host.CreateDefaultBuilder()
                .ConfigureServices((context, services) =>
                {
                    //services.AddSingleton<NvService>();
                    services.RegisterSharedServices();
                    services.AddSingleton<LgService>();
                    services.AddSingleton<GameService>();
                    services.AddSingleton<SamsungService>();
                    services.AddSingleton<PowerEventDispatcher>();
                    services.AddSingleton<ProcessEventDispatcher>();
                    services.AddSingleton<MainForm>();
                    services.AddSingleton<ElevatedForm>();
                    services.AddSingleton<LogWindow>();
                    services.AddSingleton<RestartDetector>();
                    services.AddSingleton<InfoPanel>();
                });
        }
        private static async Task RunService(string[] args)
        {
            Logger.Debug("RUNNING SERVICE");

            using IHost host = Host.CreateDefaultBuilder(args)
                .UseWindowsService(options =>
                {
                    options.ServiceName = "ColorControl Service";
                })
                .ConfigureServices(services =>
                {
                    services.RegisterSharedServices();
                    services.AddHostedService<ColorControlBackgroundService>();
                })
                .Build();

            ServiceProvider = host.Services;

            var appContextProvider = ServiceProvider.GetRequiredService<AppContextProvider>();
            AppContext = new GlobalContext(null, new StartUpParams(), DataDir, _loggingRule);
            appContextProvider.SetAppContext(AppContext);

            await host.RunAsync();
        }

        private static void RegisterSharedServices(this IServiceCollection services)
        {
            services.AddSingleton<AppContextProvider>();
            services.AddSingleton<SessionSwitchDispatcher>();
            services.AddSingleton<WinApiAdminService>();
            services.AddSingleton<WinApiService>();
            services.AddSingleton<WinElevatedProcessManager>();
            services.AddTransient<RpcClientService>();
            services.AddTransient<RpcServerService>();
            services.AddSingleton<NvService>();
            services.AddSingleton<WolService>();
            services.AddSingleton<ColorControlBackgroundService>();
            services.AddSingleton<ServiceManager>();

            RpcServerService.ServiceTypes.Add(typeof(NvService));
        }

        public static int EnumThreadWindows(IntPtr handle, IntPtr param)
        {
            NativeMethods.SendMessageW(handle, Utils.WM_BRINGTOFRONT, UIntPtr.Zero, IntPtr.Zero);

            return 1;
        }

        private static void GlobalUnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e)
        {
            GlobalHandleException((Exception)e.ExceptionObject, "Unhandled exception");
        }

        private static void GlobalThreadExceptionHandler(object sender, ThreadExceptionEventArgs e)
        {
            GlobalHandleException(e.Exception, "Exception in thread");
        }

        private static void GlobalHandleException(Exception exception, string type)
        {
            if (UserExit)
            {
                return;
            }

            var trace = exception.ToLogString(Environment.StackTrace);
            var message = $"{type}: {trace}";

            Logger.Error(message);

            MessageBox.Show(message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
