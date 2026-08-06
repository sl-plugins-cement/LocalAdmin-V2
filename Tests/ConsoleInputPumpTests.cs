using LocalAdmin.V2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LocalAdmin.V2.Tests;

public sealed class ConsoleInputPumpTests
{
    [Fact]
    public async Task Stops_After_Eof_Instead_Of_Spinning()
    {
        var reader = new CountingReader("players\n\nrestartwhen status\n");
        var commands = new List<string>();
        var eofCount = 0;
        var pump = new ConsoleInputPump(reader, commands.Add, () => eofCount++);

        await pump.Start().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "players", "restartwhen status" }, commands);
        Assert.Equal(4, reader.ReadCount); // three lines plus the single EOF read
        Assert.Equal(1, eofCount);
    }

    [Fact]
    public async Task Start_Is_Idempotent_While_Reader_Is_Blocked()
    {
        using var reader = new BlockingEofReader();
        var pump = new ConsoleInputPump(reader, _ => { });

        var first = pump.Start();
        Assert.True(reader.ReadStarted.Wait(TimeSpan.FromSeconds(2)));
        var second = pump.Start();

        Assert.Same(first, second);
        Assert.Equal(1, reader.ReadCount);

        reader.Release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, reader.ReadCount);
    }

    private sealed class CountingReader : StringReader
    {
        internal CountingReader(string value) : base(value) { }

        internal int ReadCount { get; private set; }

        public override string? ReadLine()
        {
            ReadCount++;
            return base.ReadLine();
        }
    }

    private sealed class BlockingEofReader : TextReader
    {
        internal readonly ManualResetEventSlim ReadStarted = new();
        internal readonly ManualResetEventSlim Release = new();
        internal int ReadCount;

        public override string? ReadLine()
        {
            Interlocked.Increment(ref ReadCount);
            ReadStarted.Set();
            Release.Wait();
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            Release.Set();
            ReadStarted.Dispose();
            Release.Dispose();
            base.Dispose(disposing);
        }
    }
}
