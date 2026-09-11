Imports System
Imports System.Text

''' <summary>
''' JSql 测试宿主：
'''   test                运行 JSONL / CSV 功能 demo（默认）
'''   test demo           同上
'''   test stress [...]   运行存储读写压力测试（默认 2GB，可调规模）
'''   test all [...]      先跑 demo 再跑 stress
''' </summary>
Module Program

    Sub Main(args As String())
        Console.OutputEncoding = New UTF8Encoding(False)

        Dim mode As String = "demo"

        If args.Length > 0 AndAlso Not args(0).StartsWith("--") Then
            mode = args(0).Trim().ToLowerInvariant()
        End If

        Dim exitCode As Integer = 0

        Select Case mode
            Case "demo"
                exitCode = StorageFormatDemoTests.Run()
            Case "sqlite"
                exitCode = StorageSqliteTests.Run()
            Case "stress"
                exitCode = StorageStressTests.Run(args)
            Case "all"
                exitCode = StorageFormatDemoTests.Run()

                Dim sqlite As Integer = StorageSqliteTests.Run()

                If sqlite <> 0 Then
                    exitCode = sqlite
                End If

                Dim stress As Integer = StorageStressTests.Run(args)

                If stress <> 0 Then
                    exitCode = stress
                End If
            Case Else
                Console.WriteLine("unknown test suite: " & mode)
                PrintUsage()
                Environment.ExitCode = 1
                Return
        End Select

        Environment.ExitCode = exitCode
    End Sub

    Private Sub PrintUsage()
        Console.WriteLine("usage: test [demo|sqlite|stress|all] [stress options]")
        Console.WriteLine("  demo                  run the jsonl/csv functional demo (default)")
        Console.WriteLine("  sqlite                run the sqlite backend end-to-end tests")
        Console.WriteLine("  stress                run the storage read/write stress test")
        Console.WriteLine("  all                   run demo, sqlite and stress")
        Console.WriteLine("stress options:")
        Console.WriteLine("  --size <GB>           target data size (default 2)")
        Console.WriteLine("  --rows <N>            explicit row count (overrides --size)")
        Console.WriteLine("  --format <f>          jsonl | csv | both (default both)")
        Console.WriteLine("  --sql-rows <N>        sql level benchmark rows per format (default 20000)")
        Console.WriteLine("  --cleanup             delete the temporary database after the run")
    End Sub

End Module
