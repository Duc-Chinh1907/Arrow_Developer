using System.Text;
using Arrow.Cli;

// P08 §3.1: UTF-8 output so Vietnamese text and the symbols of rich mode print correctly on Windows.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cancel = new CancellationTokenSource();
var app = new CliApp(CliConsole.System());
Console.CancelKeyPress += (_, e) =>
{
    // Ctrl+C cancels the running command (exit code 3); the process ends on its own after cleanup. Inside the interactive
    // session it cancels only the command and the session goes on (P08 §5.4).
    e.Cancel = true;
    if (!app.Interrupt())
    {
        cancel.Cancel();
    }
};

try
{
    return await app.RunAsync(args, cancel.Token).ConfigureAwait(false);
}
catch (Exception ex)
{
    // Last line of defence (P08 §9): an exception outside a command, e.g. while starting up.
    await Console.Error.WriteLineAsync("[X] CLI_INTERNAL · Lỗi nội bộ của ARROW: " + ex.Message).ConfigureAwait(false);
    return ExitCodes.Internal;
}
