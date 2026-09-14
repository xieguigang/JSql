Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Threading
Imports JSql.Engine
Imports JSql.Storage
Imports Microsoft.VisualBasic.Data.Repository

''' <summary>
''' 多进程访问回归：
''' <list type="bullet">
''' <item>底层锁语义：独占冲突立即失败、等待重试、超时、共享读共存、读者与写者互斥；</item>
''' <item>引擎级交替访问：多进程模式下两个引擎实例可交替在同一张表上执行语句；</item>
''' <item>索引缓存失效：另一实例写入后，本实例的索引查询不会漏行；</item>
''' <item>真实跨进程：子进程持锁时父进程快速失败，子进程释放后父进程写入成功。</item>
''' </list>
''' </summary>
Module StorageMultiProcessTests

    Private passed As Integer
    Private failed As Integer

    Public Function Run() As Integer
        passed = 0
        failed = 0

        Dim root As String = Path.Combine(Path.GetTempPath(), "jsql-mp-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)

        Console.WriteLine("==== JSql multi-process access tests ====")
        Console.WriteLine("work dir: " & root)
        Console.WriteLine()

        Try
            RunLockSemantics(root)
            RunInterleavedEngines(root)
            RunIndexCacheInvalidation(root)
            RunCrossProcessLock(root)
        Finally
            Try : Directory.Delete(root, recursive:=True) : Catch : End Try
        End Try

        Console.WriteLine()
        Console.WriteLine($"  passed: {passed}, failed: {failed}")
        Console.WriteLine()

        Return If(failed = 0, 0, 1)
    End Function

#Region "底层锁语义"

    Private Sub RunLockSemantics(root As String)
        Console.WriteLine("-- raw store lock semantics --")

        Dim dir As String = Path.Combine(root, "locks")
        Directory.CreateDirectory(dir)

        Dim dataPath As String = Path.Combine(dir, "t.jsonl")

        ' 1) 默认配置（独占 + 超时 0）：冲突立即失败
        Dim holder As New TextLineStore(dataPath, New TextStoreOptions())
        holder.Open()

        Try
            Dim failedFast As Boolean = False
            Dim message As String = ""

            Try
                Using other As New TextLineStore(dataPath, New TextStoreOptions())
                    other.Open()
                End Using
            Catch ex As IOException
                failedFast = True
                message = ex.Message
            End Try

            Check("exclusive: conflicting open fails immediately", failedFast)
            Check("exclusive: error message names the lock file",
                  message.Contains(".lock"), message)
        Finally
            holder.Dispose()
        End Try

        ' 2) 等待重试：持锁者在等待期间释放后应成功获取
        Dim holder2 As New TextLineStore(dataPath, New TextStoreOptions())
        holder2.Open()

        Dim acquired As Boolean = False
        Dim waitError As String = ""
        Dim waitOptions As New TextStoreOptions With {.LockWaitTimeoutMs = 5000, .LockRetryIntervalMs = 20}
        Dim waiter As New Thread(
            Sub()
                Try
                    Using store As New TextLineStore(dataPath, waitOptions)
                        store.Open()
                        acquired = True
                    End Using
                Catch ex As Exception
                    waitError = ex.GetType().Name & ": " & ex.Message
                End Try
            End Sub)
        waiter.IsBackground = True
        waiter.Start()

        Thread.Sleep(250)
        Check("wait: still blocked while the lock is held", Not acquired)

        holder2.Dispose()
        waiter.Join(5000)
        Check("wait: acquired after the holder released", acquired, waitError)

        ' 3) 超时：持锁者不释放时应抛出明确错误
        Dim holder3 As New TextLineStore(dataPath, New TextStoreOptions())
        holder3.Open()

        Dim timedOut As Boolean = False
        Dim watch As Stopwatch = Stopwatch.StartNew()

        Try
            Using other As New TextLineStore(dataPath, New TextStoreOptions With {.LockWaitTimeoutMs = 200, .LockRetryIntervalMs = 20})
                other.Open()
            End Using
        Catch ex As IOException
            timedOut = True
        End Try

        watch.Stop()
        holder3.Dispose()

        Check("wait: timeout reported", timedOut)
        Check("wait: waited roughly the timeout", watch.ElapsedMilliseconds >= 150, watch.ElapsedMilliseconds & "ms")

        ' 4) 共享读：两个只读实例可以共存
        Dim sharedOptions As New TextStoreOptions With {.LockMode = TextStoreLockMode.SharedRead}
        Dim sharedOk As Boolean = False
        Dim sharedError As String = ""

        Try
            Using reader1 As New TextLineStore(dataPath, sharedOptions)
                reader1.Open()

                Using reader2 As New TextLineStore(dataPath, sharedOptions)
                    reader2.Open()
                    sharedOk = True
                End Using
            End Using
        Catch ex As Exception
            sharedError = ex.GetType().Name & ": " & ex.Message
        End Try

        Check("shared: two readers coexist", sharedOk, sharedError)

        ' 5) 共享读与独占写互斥
        Dim blocked As Boolean = False

        Try
            Using reader As New TextLineStore(dataPath, sharedOptions)
                reader.Open()

                Try
                    Using writer As New TextLineStore(dataPath, New TextStoreOptions())
                        writer.Open()
                    End Using
                Catch ex As IOException
                    blocked = True
                End Try
            End Using
        Catch ex As Exception
        End Try

        Check("shared: a reader blocks the exclusive writer", blocked)
    End Sub

#End Region

#Region "引擎级交替访问"

    Private Function MultiProcessOptions() As StorageOptions
        Return New StorageOptions With {
            .MultiProcessAccess = True,
            .MergeIdleSeconds = 0,
            .MergeOnStatementEnd = True,
            .LockConflictPolicy = LockConflictPolicy.Wait,
            .LockWaitTimeoutMs = 5000,
            .LockRetryIntervalMs = 25
        }
    End Function

    Private Sub RunInterleavedEngines(root As String)
        Console.WriteLine("-- interleaved engines (multi-process mode) --")

        Dim dbDir As String = Path.Combine(root, "interleaved")
        Directory.CreateDirectory(dbDir)

        Dim first As New SqlEngine(dbDir, MultiProcessOptions())
        Dim second As New SqlEngine(dbDir, MultiProcessOptions())

        Try
            first.ExecuteBatch("CREATE DATABASE shop; USE shop; CREATE TABLE t (id INT NOT NULL PRIMARY KEY, v VARCHAR(20));")

            ' 当前数据库是每个引擎各自的运行时状态
            second.Execute("USE shop")

            ' 每个语句结束都会释放锁，因此两个引擎可以交替写入同一张表
            first.Execute("INSERT INTO t (id, v) VALUES (1, 'a1')")
            second.Execute("INSERT INTO t (id, v) VALUES (2, 'b1')")
            first.Execute("INSERT INTO t (id, v) VALUES (3, 'a2')")

            Dim verify As New SqlEngine(dbDir, MultiProcessOptions())

            Try
                verify.Execute("USE shop")

                Dim count As ResultSet = verify.Execute("SELECT COUNT(*) FROM t")
                Check("interleaved: all three inserts are visible", CInt(CLng(count.Rows(0)(0))) = 3,
                      "count=" & CStr(count.Rows(0)(0)))
            Finally
                verify.Dispose()
            End Try

            ' 反例：非多进程模式的引擎会在语句之间继续持有锁，另一个引擎无法写入
            Dim exclusive As New SqlEngine(dbDir, New StorageOptions With {.MergeIdleSeconds = 0})
            exclusive.Execute("USE shop")
            exclusive.Execute("SELECT COUNT(*) FROM t")

            Dim failFastOptions As StorageOptions = MultiProcessOptions()
            failFastOptions.LockConflictPolicy = LockConflictPolicy.FailFast

            Dim blocked As Boolean = False
            Dim blockedEngine As New SqlEngine(dbDir, failFastOptions)

            Try
                blockedEngine.Execute("USE shop")
                blockedEngine.Execute("SELECT COUNT(*) FROM t")
            Catch ex As Exception
                blocked = True
            Finally
                blockedEngine.Dispose()
                exclusive.Dispose()
            End Try

            Check("interleaved: a non-multi-process engine keeps an exclusive lock", blocked)
        Finally
            first.Dispose()
            second.Dispose()
        End Try
    End Sub

#End Region

#Region "索引缓存失效"

    Private Sub RunIndexCacheInvalidation(root As String)
        Console.WriteLine("-- index cache invalidation across engines --")

        Dim dbDir As String = Path.Combine(root, "idxmp")
        Directory.CreateDirectory(dbDir)

        Dim first As New SqlEngine(dbDir, MultiProcessOptions())
        Dim second As New SqlEngine(dbDir, MultiProcessOptions())

        Try
            first.ExecuteBatch("CREATE DATABASE d; USE d; " &
                               "CREATE TABLE u (id INT NOT NULL PRIMARY KEY, name VARCHAR(30) NOT NULL);")
            first.Execute("CREATE INDEX idx_u_name ON u (name) USING HASH")
            first.Execute("INSERT INTO u (id, name) VALUES (1, 'alpha')")

            ' 先查询一次，让第一个引擎构建并缓存索引
            Dim initial As ResultSet = first.Execute("SELECT name FROM u WHERE name = 'alpha'")
            Check("index: initial indexed query", initial.Rows.Count = 1, "rows=" & initial.Rows.Count)

            ' 第二个引擎写入新数据：第一个引擎的索引缓存此时已过期
            second.Execute("USE d")
            second.Execute("INSERT INTO u (id, name) VALUES (2, 'beta')")

            ' 若索引缓存未失效，这里会因为候选集漏行而查不到 beta
            Dim after As ResultSet = first.Execute("SELECT name FROM u WHERE name = 'beta'")
            Check("index: stale cache does not hide another engine's row",
                  after.Rows.Count = 1 AndAlso CStr(after.Rows(0)(0)) = "beta",
                  "rows=" & after.Rows.Count)
        Finally
            first.Dispose()
            second.Dispose()
        End Try
    End Sub

#End Region

#Region "真实跨进程锁"

    Private Sub RunCrossProcessLock(root As String)
        Console.WriteLine("-- real cross process lock --")

        Dim dbDir As String = Path.Combine(root, "xproc")
        Directory.CreateDirectory(dbDir)

        ' 准备数据库与表
        Dim prepare As New SqlEngine(dbDir, New StorageOptions With {.MergeIdleSeconds = 0})

        Try
            prepare.ExecuteBatch("CREATE DATABASE p; USE p; CREATE TABLE t (id INT NOT NULL PRIMARY KEY, v VARCHAR(20));")
            prepare.Execute("INSERT INTO t (id, v) VALUES (1, 'seed')")
        Finally
            prepare.Dispose()
        End Try

        Dim child As Process = StartLockHolder(dbDir, 3000)

        If child Is Nothing Then
            Check("cross process: child process started", False, "unable to start the test host")
            Return
        End If

        Try
            ' 等待子进程取得锁（子进程在取得锁后打印一行）
            Dim ready As String = child.StandardOutput.ReadLine()
            Check("cross process: child acquired the lock",
                  ready IsNot Nothing AndAlso ready.Contains("lock acquired"), If(ready, "<no output>"))

            ' 父进程使用快速失败策略：应立刻失败
            Dim failFastOptions As StorageOptions = MultiProcessOptions()
            failFastOptions.LockConflictPolicy = LockConflictPolicy.FailFast

            Dim failed As Boolean = False
            Dim probe As New SqlEngine(dbDir, failFastOptions)

            Try
                probe.Execute("USE p")
                probe.Execute("INSERT INTO t (id, v) VALUES (2, 'parent')")
            Catch ex As Exception
                failed = True
            Finally
                probe.Dispose()
            End Try

            Check("cross process: fail fast while the child holds the lock", failed)

            ' 子进程退出（释放锁）后，等待策略应能成功写入
            Check("cross process: child exits", child.WaitForExit(20000))

            Dim ok As Boolean = False
            Dim writer As New SqlEngine(dbDir, MultiProcessOptions())

            Try
                writer.Execute("USE p")
                writer.Execute("INSERT INTO t (id, v) VALUES (3, 'parent2')")

                Dim rows As ResultSet = writer.Execute("SELECT v FROM t WHERE id = 3")
                ok = rows.Rows.Count = 1 AndAlso CStr(rows.Rows(0)(0)) = "parent2"
            Finally
                writer.Dispose()
            End Try

            Check("cross process: write succeeds after the child released the lock", ok)
        Finally
            Try
                If Not child.HasExited Then child.Kill(entireProcessTree:=True)
            Catch
            End Try

            child.Dispose()
        End Try
    End Sub

    ''' <summary>
    ''' 启动一个子进程（本测试宿主 + <c>hold</c> 模式），它以独占模式打开数据库并保持表锁。
    ''' </summary>
    Private Function StartLockHolder(dbDir As String, milliseconds As Integer) As Process
        Try
            Dim host As String = Environment.ProcessPath
            Dim entry As String = Assembly.GetEntryAssembly().Location

            Dim start As New ProcessStartInfo With {
                .FileName = host,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .WorkingDirectory = AppContext.BaseDirectory
            }

            ' `dotnet test.dll ...` 时需要把程序集路径作为第一个参数
            If Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) Then
                start.ArgumentList.Add(entry)
            End If

            start.ArgumentList.Add("hold")
            start.ArgumentList.Add("--db")
            start.ArgumentList.Add(dbDir)
            start.ArgumentList.Add("--ms")
            start.ArgumentList.Add(milliseconds.ToString())

            Return Process.Start(start)
        Catch ex As Exception
            Console.WriteLine("         unable to start child process: " & ex.Message)
            Return Nothing
        End Try
    End Function

    ''' <summary>
    ''' 测试辅助入口（子进程）：以独占模式打开数据库并保持表锁若干毫秒。
    ''' 用法：<c>test hold --db &lt;dir&gt; --ms &lt;n&gt;</c>
    ''' </summary>
    Public Function RunLockHolderWorker(args As String()) As Integer
        Dim dbDir As String = Nothing
        Dim milliseconds As Integer = 1000

        For i As Integer = 0 To args.Length - 1
            Select Case args(i)
                Case "--db"
                    If i + 1 < args.Length Then dbDir = args(i + 1)
                Case "--ms"
                    If i + 1 < args.Length Then Integer.TryParse(args(i + 1), milliseconds)
            End Select
        Next

        If String.IsNullOrEmpty(dbDir) Then
            Console.Error.WriteLine("hold: --db <dir> is required")
            Return 2
        End If

        ' 非多进程模式：语句结束后会话仍被缓存，因此表锁会一直保持到进程退出
        Using engine As New SqlEngine(dbDir, New StorageOptions With {.MergeIdleSeconds = 0})
            engine.Execute("USE p")

            Dim count As ResultSet = engine.Execute("SELECT COUNT(*) FROM t")
            Console.WriteLine("hold: lock acquired (" & CStr(count.Rows(0)(0)) & " rows), sleeping " & milliseconds & " ms")
            Console.Out.Flush()

            Thread.Sleep(Math.Max(0, milliseconds))
        End Using

        Return 0
    End Function

#End Region

#Region "helpers"

    Private Sub Check(name As String, condition As Boolean, Optional detail As String = "")
        If condition Then
            passed += 1
            Console.WriteLine($"  [PASS] {name}")
        Else
            failed += 1
            Console.WriteLine($"  [FAIL] {name}")

            If Not String.IsNullOrEmpty(detail) Then
                Console.WriteLine($"         {detail}")
            End If
        End If
    End Sub

#End Region

End Module
