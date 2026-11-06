// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using MicroBenchmarks;

#pragma warning disable CS1998 // async methods without awaits

namespace System.Threading.Tasks.Tests
{
    /// <summary>
    /// Measures what a chain of async methods costs when it usually completes synchronously, as in an incremental
    /// backup: stat each file, and only read it if the stat doesn't match what was recorded earlier. On a match no
    /// method in the chain suspends, so the cost is the chain itself. The stat is real, because it has no async API.
    ///
    ///     Scan -> Layer x (Frames - 2) -> CheckFile -> Stat          match: Frames async calls per file
    ///                                            -> ReadFile   mismatch: a real suspension
    ///
    /// To compare state machines with runtime async, run this with and without runtime-async=on in the project's
    /// Features property.
    /// </summary>
    [BenchmarkCategory(Categories.Libraries, Categories.NoWASM)]
    public class Perf_AsyncSyncCompletion
    {
        private const int FileCount = 1_000;
        private const int FileSize = 1_024;

        private readonly byte[] _buffer = new byte[FileSize];
        private string _directory;
        private Entry[] _entries;

        [Params(3, 5, 7)]
        public int Frames { get; set; }

        [Params(100, 99)]
        public int HitPercent { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _directory = FileUtils.GetTestFilePath();
            Directory.CreateDirectory(_directory);

            byte[] content = new byte[FileSize];
            int missEvery = HitPercent == 100 ? int.MaxValue : 100 / (100 - HitPercent);

            _entries = new Entry[FileCount];
            for (int i = 0; i < FileCount; i++)
            {
                string path = Path.Combine(_directory, i.ToString());
                File.WriteAllBytes(path, content);

                long recorded = File.GetLastWriteTimeUtc(path).Ticks;
                _entries[i] = new Entry(path, i % missEvery == missEvery - 1 ? recorded - 1 : recorded);
            }
        }

        [GlobalCleanup]
        public void Cleanup() => Directory.Delete(_directory, recursive: true);

        [Benchmark(OperationsPerInvoke = FileCount)]
        public async Task<long> Scan()
        {
            int depth = Frames - 3;
            long total = 0;
            foreach (Entry entry in _entries)
            {
                total += await Layer(entry, depth);
            }
            return total;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<long> Layer(Entry entry, int depth)
        {
            if (depth == 0)
            {
                return await CheckFile(entry);
            }

            long result = await Layer(entry, depth - 1);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<long> CheckFile(Entry entry)
        {
            long modified = await Stat(entry);
            if (modified == entry.Recorded)
            {
                return modified;
            }

            return await ReadFile(entry);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<long> Stat(Entry entry) => File.GetLastWriteTimeUtc(entry.Path).Ticks;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private async Task<long> ReadFile(Entry entry)
        {
            using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, useAsync: true);
            return await stream.ReadAsync(_buffer, 0, _buffer.Length);
        }

        private sealed class Entry(string path, long recorded)
        {
            public string Path { get; } = path;
            public long Recorded { get; } = recorded;
        }
    }
}
