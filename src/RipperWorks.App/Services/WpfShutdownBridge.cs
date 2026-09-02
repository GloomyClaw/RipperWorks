using System.Windows.Threading;

namespace RipperWorks.App.Services;

public static class WpfShutdownBridge
{
    public static void Wait(
        Dispatcher dispatcher,
        Task shutdown,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (!dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "The WPF shutdown bridge must run on its Dispatcher thread.");
        }
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (shutdown.IsCompleted)
        {
            shutdown.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        var timedOut = false;
        var timer = new DispatcherTimer(
            DispatcherPriority.Send,
            dispatcher)
        {
            Interval = timeout
        };
        EventHandler timeoutHandler = (_, _) =>
        {
            timedOut = true;
            frame.Continue = false;
        };
        timer.Tick += timeoutHandler;
        shutdown.GetAwaiter().OnCompleted(() =>
        {
            try
            {
                dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false));
            }
            catch (InvalidOperationException)
            {
                frame.Continue = false;
            }
        });

        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
            timer.Tick -= timeoutHandler;
        }

        if (timedOut && !shutdown.IsCompleted)
        {
            throw new TimeoutException(
                $"WPF shutdown did not complete within " +
                $"{timeout.TotalMilliseconds:F0} ms.");
        }
        shutdown.GetAwaiter().GetResult();
    }
}
