using Misdirection.Sender;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C stops sending cleanly (with a PANIC); a second one kills the process.
    if (cts.IsCancellationRequested) return;
    e.Cancel = true;
    cts.Cancel();
};

return await new Cli(Console.Out, Console.Error).RunAsync(args, cts.Token);
