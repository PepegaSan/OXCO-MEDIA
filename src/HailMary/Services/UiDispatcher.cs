namespace HailMary.Services;

public static class UiDispatcher
{
    public static void Run(Action action)
    {
        var queue = App.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        queue.TryEnqueue(() => action());
    }
}
