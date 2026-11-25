using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MatMul
{
    public sealed class ProgressTracker
    {
        public int TotalRows = 0;
        public int CompletedRows = 0;
        public int NextRowDynamic = 0;
    }

    internal static class Program
    {
        private const long MOD = 1_000_000_007;

        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "generate")
            {
                RunGenerator(args);
                return;
            }

            if (args.Length < 5)
            {
                Console.WriteLine("Использование: MatMul <n> <m> <k> <threads> <static|dynamic> [power]");
                Console.WriteLine("Примеры:");
                Console.WriteLine("  MatMul 1000 1000 1000 8 static      (Обычное умножение)");
                Console.WriteLine("  MatMul 1000 1000 1000 8 dynamic tri  (Треугольная матрица)");
                Console.WriteLine("  MatMul 300 300 300 8 static 5       (Возведение в степень 5)");
                return;
            }

            int n = int.Parse(args[0]);
            int m = int.Parse(args[1]);
            int k = int.Parse(args[2]);
            int T = int.Parse(args[3]);
            bool isDynamic = args[4] == "dynamic";
            bool isTriangular = args.Length > 5 && args[5] == "tri";
            int powerP = 1;
            if (args.Length > 6 && int.TryParse(args[6], out int p)) powerP = Math.Max(1, p);

            string genMode = (powerP > 1) ? "single" : (isTriangular ? "tri" : "normal");
            
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    Arguments = $"generate {n} {m} {k} {genMode}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();

            using var reader = new BinaryReader(proc.StandardOutput.BaseStream);
            long[][] matrixA = ReadMatrix(reader);
            long[][] matrixB = (genMode == "single") ? matrixA : ReadMatrix(reader);
            proc.WaitForExit();

            var tracker = new ProgressTracker();
            tracker.TotalRows = (powerP > 1) ? n * (powerP - 1) : n;

            var progressThread = new Thread(() =>
            {
                while (tracker.CompletedRows < tracker.TotalRows)
                {
                    int pct = (int)((double)tracker.CompletedRows / tracker.TotalRows * 100);
                    Console.Write($"\rПрогресс: {pct}%");
                    Thread.Sleep(200);
                }
                Console.WriteLine("\rПрогресс: 100%");
            });
            progressThread.Start();

            var sw = Stopwatch.StartNew();
            long[][] result;

            if (powerP > 1)
            {
                result = PowerParallel(matrixA, powerP, T, isDynamic, tracker);
            }
            else
            {
                result = MultiplyParallel(matrixA, matrixB, T, isDynamic, tracker);
            }

            sw.Stop();
            progressThread.Join();

            long checksum = 0;
            foreach (var row in result)
                foreach (long val in row)
                    checksum = (checksum + val) % MOD;

            Console.WriteLine($"\nВремя выполнения: {sw.ElapsedMilliseconds} мс");
            Console.WriteLine($"Контрольная сумма: {checksum}");

            RunBuiltInTests();
        }

        private static void RunGenerator(string[] args)
        {
            int n = int.Parse(args[1]), m = int.Parse(args[2]), k = int.Parse(args[3]);
            string mode = args.Length > 4 ? args[4] : "normal";
            var rnd = new Random(42);
            
            using var writer = new BinaryWriter(Console.OpenStandardOutput());
            
            if (mode == "single")
            {
                WriteMatrix(writer, Generate(n, n, rnd));
            }
            else if (mode == "tri")
            {
                WriteMatrix(writer, Generate(n, n, rnd, triangular: true));
                WriteMatrix(writer, Generate(n, k, rnd));
            }
            else
            {
                WriteMatrix(writer, Generate(n, m, rnd));
                WriteMatrix(writer, Generate(m, k, rnd));
            }
            writer.Flush();
        }

        private static long[][] Generate(int rows, int cols, Random rnd, bool triangular = false)
        {
            long[][] mat = new long[rows][];
            for (int i = 0; i < rows; i++)
            {
                int len = triangular ? i + 1 : cols;
                mat[i] = new long[len];
                for (int j = 0; j < len; j++) mat[i][j] = rnd.Next(0, 10);
            }
            return mat;
        }

        private static void WriteMatrix(BinaryWriter w, long[][] m)
        {
            w.Write(m.Length);
            foreach (var row in m)
            {
                w.Write(row.Length);
                foreach (var v in row) w.Write(v);
            }
        }

        private static long[][] ReadMatrix(BinaryReader r)
        {
            int rows = r.ReadInt32();
            long[][] m = new long[rows][];
            for (int i = 0; i < rows; i++)
            {
                int len = r.ReadInt32();
                m[i] = new long[len];
                for (int j = 0; j < len; j++) m[i][j] = r.ReadInt64();
            }
            return m;
        }

        private static void MultiplyRow(long[][] A, long[][] B, long[][] C, int i, bool useMod)
        {
            int k = C[i].Length;
            for (int j = 0; j < k; j++) C[i][j] = 0;

            int limitA = A[i].Length;
            for (int t = 0; t < limitA; t++)
            {
                long a = A[i][t];
                if (a == 0) continue;
                
                long[] bRow = B[t];
                for (int j = 0; j < k; j++)
                {
                    long val = C[i][j] + a * bRow[j];
                    C[i][j] = useMod ? val % MOD : val;
                }
            }
        }

        private static long[][] MultiplyParallel(long[][] A, long[][] B, int T, bool isDynamic, ProgressTracker tr)
        {
            int n = A.Length, k = B[0].Length;
            long[][] C = new long[n][];
            for (int i = 0; i < n; i++) C[i] = new long[k];

            Thread[] workers = new Thread[T];
            for (int t = 0; t < T; t++)
            {
                int id = t;
                workers[t] = new Thread(() =>
                {
                    if (isDynamic)
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref tr.NextRowDynamic) - 1;
                            if (i >= n) break;
                            MultiplyRow(A, B, C, i, false);
                            Interlocked.Increment(ref tr.CompletedRows);
                        }
                    }
                    else
                    {
                        int chunk = (n + T - 1) / T;
                        int start = id * chunk;
                        int end = Math.Min(start + chunk, n);
                        for (int i = start; i < end; i++)
                        {
                            MultiplyRow(A, B, C, i, false);
                            Interlocked.Increment(ref tr.CompletedRows);
                        }
                    }
                });
                workers[t].Start();
            }
            foreach (var w in workers) w.Join();
            return C;
        }

        private static long[][] PowerParallel(long[][] A, int p, int T, bool isDynamic, ProgressTracker tr)
        {
            int n = A.Length;
            if (p == 1) return A;

            long[][] buf1 = new long[n][];
            long[][] buf2 = new long[n][];
            for (int i = 0; i < n; i++) { buf1[i] = new long[n]; buf2[i] = new long[n]; }

            Barrier barrier = new Barrier(T); 
            
            Thread[] workers = new Thread[T];
            for (int t = 0; t < T; t++)
            {
                int id = t;
                workers[t] = new Thread(() =>
                {
                    long[][] readBuf = A;
                    long[][] writeBuf = buf1;

                    for (int step = 0; step < p - 1; step++)
                    {
                        if (isDynamic)
                        {
                            while (true)
                            {
                                int i = Interlocked.Increment(ref tr.NextRowDynamic) - 1;
                                if (i >= n) break;
                                MultiplyRow(readBuf, A, writeBuf, i, true);
                                Interlocked.Increment(ref tr.CompletedRows);
                            }
                        }
                        else
                        {
                            int chunk = (n + T - 1) / T;
                            int start = id * chunk;
                            int end = Math.Min(start + chunk, n);
                            for (int i = start; i < end; i++)
                            {
                                MultiplyRow(readBuf, A, writeBuf, i, true);
                                Interlocked.Increment(ref tr.CompletedRows);
                            }
                        }

                        barrier.SignalAndWait();

                        if (id == 0)
                        {
                            Interlocked.Exchange(ref tr.NextRowDynamic, 0);
                            long[][] temp = readBuf;
                            readBuf = writeBuf;
                            writeBuf = (writeBuf == buf1) ? buf2 : buf1;
                        }
                        
                        barrier.SignalAndWait();
                    }
                });
                workers[t].Start();
            }
            foreach (var w in workers) w.Join();
            
            return ((p - 2) % 2 == 0) ? buf1 : buf2;
        }

        private static void RunBuiltInTests()
        {
            Console.WriteLine("\n--- ЗАПУСК АВТОМАТИЧЕСКИХ ТЕСТОВ ---");
            int passed = 0, failed = 0;

            try
            {
                var A = new[] { new long[] { 1, 2 }, new long[] { 3, 4 } };
                var B = new[] { new long[] { 5, 6 }, new long[] { 7, 8 } };
                var res = MultiplyParallel(A, B, 2, false, new ProgressTracker());
                if (res[0][0] == 19 && res[0][1] == 22 && res[1][0] == 43 && res[1][1] == 50) passed++;
                else throw new Exception("Неверный результат малой матрицы");
            } catch { failed++; Console.WriteLine("[FAIL] Тест 1: Малая матрица"); }

            try
            {
                var E = new[] { new long[] { 1, 0 }, new long[] { 0, 1 } };
                var A = new[] { new long[] { 5, 2 }, new long[] { 3, 7 } };
                var res = MultiplyParallel(E, A, 4, true, new ProgressTracker());
                if (res[0][0] == 5 && res[1][1] == 7) passed++;
                else throw new Exception("Единичная матрица изменила данные");
            } catch { failed++; Console.WriteLine("[FAIL] Тест 2: Единичная матрица"); }

            try
            {
                var rnd = new Random(42);
                var Tri = Generate(5, 5, rnd, triangular: true);
                var B = Generate(5, 3, rnd);
                var resStatic = MultiplyParallel(Tri, B, 3, false, new ProgressTracker());
                var resDynamic = MultiplyParallel(Tri, B, 3, true, new ProgressTracker());
                
                bool match = true;
                for(int i=0; i<5; i++) for(int j=0; j<3; j++) if(resStatic[i][j] != resDynamic[i][j]) match = false;
                if(match) passed++; else throw new Exception("Расхождение статики и динамики");
            } catch { failed++; Console.WriteLine("[FAIL] Тест 3: Треугольная матрица"); }

            try
            {
                var A = new[] { new long[] { 1, 1 }, new long[] { 1, 1 } };
                var res = PowerParallel(A, 2, 2, false, new ProgressTracker());
                if (res[0][0] == 2 && res[1][1] == 2) passed++;
                else throw new Exception("Ошибка возведения в степень");
            } catch { failed++; Console.WriteLine("[FAIL] Тест 4: Возведение в степень"); }

            try
            {
                var rnd = new Random(1);
                var A = Generate(20, 20, rnd);
                var B = Generate(20, 20, rnd);
                var expected = MultiplyParallel(A, B, 1, false, new ProgressTracker());
                
                bool stable = true;
                for(int run = 0; run < 20; run++) 
                {
                    var res = MultiplyParallel(A, B, 8, true, new ProgressTracker());
                    for(int i=0; i<20; i++) for(int j=0; j<20; j++) 
                        if(expected[i][j] != res[i][j]) stable = false;
                }
                if(stable) passed++; else throw new Exception("Нестабильный многопоточный результат");
            } catch { failed++; Console.WriteLine("[FAIL] Тест 5: Стабильность потоков"); }

            Console.WriteLine($"РЕЗУЛЬТАТ ТЕСТОВ: Пройдено {passed}/5, Ошибок {failed}/5");
            if (failed == 0) Console.WriteLine("ВСЕ ТЕСТЫ ПРОЙДЕНЫ УСПЕШНО!");
        }
    }
}