using System.Text;

namespace CoffeeShopLab2.Shared;

public sealed class LineFramer
{
    private readonly int _maxLength;
    private byte[] _buffer;
    private int _length;
    private bool _discarding;

    public LineFramer(int maxLength)
    {
        _maxLength = maxLength;
        _buffer = new byte[Math.Min(maxLength + 64, 4096)];
        _length = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        EnsureCapacity(_length + data.Length);
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    public bool TryTake(out string line, out bool wasTooLong)
    {
        line = string.Empty;
        wasTooLong = false;

        int newline = Array.IndexOf(_buffer, Protocol.Terminator, 0, _length);

        if (newline < 0)
        {
            if (_length > _maxLength)
            {
                _length = 0;
                _discarding = true;
            }

            return false;
        }

        if (_discarding)
        {
            Consume(newline + 1);
            _discarding = false;
            wasTooLong = true;
            return true;
        }

        if (newline > _maxLength)
        {
            Consume(newline + 1);
            wasTooLong = true;
            return true;
        }

        int lineLength = newline;

        if (lineLength > 0 && _buffer[lineLength - 1] == (byte)'\r')
        {
            lineLength--;
        }

        line = Encoding.UTF8.GetString(_buffer, 0, lineLength);
        Consume(newline + 1);
        return true;
    }

    private void Consume(int count)
    {
        int remaining = _length - count;

        if (remaining > 0)
        {
            Buffer.BlockCopy(_buffer, count, _buffer, 0, remaining);
        }

        _length = remaining;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer.Length)
        {
            return;
        }

        int size = _buffer.Length;

        while (size < needed)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }
}
