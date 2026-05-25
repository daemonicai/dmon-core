using System.Text;
using System.Threading.Channels;

namespace Dmon.Terminal;

internal sealed class InputReader
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private volatile bool _isLocked;

    public bool IsLocked
    {
        get => _isLocked;
        set => _isLocked = value;
    }

    public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        StringBuilder buffer = new();

        try
        {
            await Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_isLocked || !Console.KeyAvailable)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    ConsoleKeyInfo key;
                    try
                    {
                        key = Console.ReadKey(intercept: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // stdin redirected or closed
                        break;
                    }

                    HandleKey(key, buffer);
                }
            }, cancellationToken);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private void HandleKey(ConsoleKeyInfo key, StringBuilder buffer)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
            {
                string line = buffer.ToString();
                buffer.Clear();
                Console.Write('\n');
                if (!IsLocked)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _history.Add(line);
                        _historyIndex = _history.Count;
                    }
                    _channel.Writer.TryWrite(line);
                }
                break;
            }

            case ConsoleKey.Backspace:
                if (buffer.Length > 0 && !IsLocked)
                {
                    buffer.Remove(buffer.Length - 1, 1);
                    Console.Write("\b \b");
                }
                break;

            case ConsoleKey.UpArrow:
                if (_history.Count > 0 && !IsLocked)
                {
                    _historyIndex = Math.Max(0, _historyIndex - 1);
                    ReplaceBufferLine(buffer, _history[_historyIndex]);
                }
                break;

            case ConsoleKey.DownArrow:
                if (_history.Count > 0 && !IsLocked)
                {
                    _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                    string replacement = _historyIndex < _history.Count
                        ? _history[_historyIndex]
                        : string.Empty;
                    ReplaceBufferLine(buffer, replacement);
                }
                break;

            case ConsoleKey.Escape:
                if (!IsLocked)
                    ReplaceBufferLine(buffer, string.Empty);
                break;

            default:
                if (!char.IsControl(key.KeyChar))
                {
                    if (!IsLocked)
                    {
                        buffer.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                }
                break;
        }
    }

    private static void ReplaceBufferLine(StringBuilder buffer, string replacement)
    {
        int currentLength = buffer.Length;
        Console.Write(new string('\b', currentLength));
        Console.Write(new string(' ', currentLength));
        Console.Write(new string('\b', currentLength));

        buffer.Clear();
        buffer.Append(replacement);
        Console.Write(replacement);
    }
}
