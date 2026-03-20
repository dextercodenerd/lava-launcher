using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Svg;
using GenericLauncher;
using GenericLauncher.Logger;
using Microsoft.Extensions.Logging;

namespace LavaLauncher.Desktop;

internal class Program
{
    private static readonly ILoggerFactory LoggerFactory = new SimpleLoggerFactory(
#if DEBUG
        LogLevel.Trace
#endif
    );

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // TODO: There is some kind of a bug in Rider and it stopped naming the Main thread as Main
        //  thread and it marks some non-existing thread as such in this project. Visual Studio
        //  doesn't have this problem. So we name the thread manually, so even Rider's debugger
        //  shows the name.
        //  Remove this like and check from time to time of it was fixed in Rider.
        //  Maybe it is related to the Environment.CurrentManagedThreadId where in this project the
        //  main thread has managed thread id of 2, instead of the usual 1.
        Thread.CurrentThread.Name = "Main Thread";
        // Statically inject logger factory, it is good enough for a small desktop application.
        App.LoggerFactory = LoggerFactory;
        var programLogger = LoggerFactory.CreateLogger(typeof(Program));
        programLogger.LogInformation("Start");
        programLogger.LogInformation("Managed TID={CurrentManagedThreadId}", Environment.CurrentManagedThreadId);

        // Enable crashing on unobserved task exceptions
        AppContext.SetSwitch("System.Threading.Tasks.TaskScheduler.ThrowOnUnobservedTaskExceptions", true);

        // Crash the app on an unobserved task exception. We don't want the app to be left in a
        // broken state. Rather crash, to stay stuck.
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            programLogger.LogCritical(e.Exception, "unobserved task exception");
            throw e.Exception;
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // This is needed for the live previews to work
        GC.KeepAlive(typeof(SvgImageExtension).Assembly);

        var builder = AppBuilder.Configure(() => new App())
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions { UseOpacitySaveLayer = true })
            .LogToTrace();

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                // Some computers crash with the default ANGLE-based HW acceleration, so we must use OpenGL or even
                // software rendering. But we are trying Vulkan now.
                RenderingMode = [Win32RenderingMode.Vulkan, Win32RenderingMode.Wgl, Win32RenderingMode.Software],
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new AvaloniaNativePlatformOptions
            {
                // OpenGL is deprecated, but it will still be better than software rendering.
                RenderingMode = [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            builder = builder.With(new X11PlatformOptions
            {
                // Linux GPU driver support varies a lot, so try both all the accelerated paths before falling back to
                // the software rendering.
                RenderingMode = [X11RenderingMode.Vulkan, X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software],
            });
        }

        return builder;
    }
}
