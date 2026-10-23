using H3VR.Converter;
using Spectre.Console;

using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; interrupt.Cancel(); };
var console = AnsiConsole.Console;
return await Cli.RunAsync(args, async (request, cancellation) =>
{
    try
    {
        return await new ConverterApplication(Repository.Find(), console).RunAsync(request, cancellation);
    }
    catch (OperationCanceledException)
    {
        console.WriteLine("Cancelled. Completed conversion stages remain available for resume.");
        return 130;
    }
    catch (Exception error)
    {
        console.Write(new Text("Conversion stopped: " + error.Message + "\n", new Style(Color.Red)));
        if (request.Options.Verbose) console.WriteException(error);
        return 1;
    }
}, console, interrupt.Token);
