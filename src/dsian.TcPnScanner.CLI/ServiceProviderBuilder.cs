using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;

namespace dsian.TcPnScanner.CLI;

internal static class ServiceProviderBuilder
{
    internal static ServiceProvider BuildServiceProvider(LoggingLevelSwitch loggingLevelSwitch, CliOptions cliOptions)
    {
        var serilogConfig = new LoggerConfiguration()
            .WriteToConsoleIf(() =>
            {
                return !Console.IsOutputRedirected;
            })
            .WriteTo.File(ResolveLogFilePath(cliOptions.LogFile), rollingInterval: RollingInterval.Hour)
            .MinimumLevel.ControlledBy(loggingLevelSwitch)
            .CreateLogger();

        var serviceProvider = new ServiceCollection()
            .AddLogging(cfg => cfg.AddSerilog(serilogConfig))
            .BuildServiceProvider();
        return serviceProvider;
    }

    private static LoggerConfiguration WriteToConsoleIf(this LoggerConfiguration logCfg, Func<bool> predicate)
    {
        if (!predicate())
        {
            return logCfg;
        }

        return logCfg.WriteTo.Console();
    }

    private static string ResolveLogFilePath(string logFilePath)
    {
        logFilePath = logFilePath.Replace(Constants.TEMP_DIR_TEMPLATE, Path.GetTempPath());
        logFilePath = logFilePath.Replace(Constants.APPNAME_TEMPLATE, AssemblyHelper.Name);
        return logFilePath;
    }
}
