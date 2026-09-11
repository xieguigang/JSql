Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports JSql.Engine
Imports JSql.Storage
Imports Microsoft.VisualBasic.Data.Repository

''' <summary>
''' JSql 存储读写压力测试。
''' <list type="bullet">
''' <item>批量写入：用底层行存储引擎分块 append + 周期 merge，构造目标规模（默认 2GB）的数据文件，
'''       测量吞吐与 checkpoint 成本（O(追加量)，不整表重写）。</item>
''' <item>顺序扫描 / 随机读取：复用稀疏行索引，测量扫描吞吐与随机行读取延迟。</item>
''' <item>局部改写：对已合并的大文件做批量 replace 并 merge，验证增量写的 O(变更量) 行为。</item>
''' <item>SQL 级基准：在可配置的较小规模上跑 INSERT/SELECT/UPDATE/DELETE 全链路，覆盖格式切换。</item>
''' </list>
''' </summary>
Module StorageStressTests

    Private Const PayloadSize As Integer = 200

    Private Class BenchResult
        Public Property Label As String
        Public Property DataRows As Long
        Public Property Bytes As Long
        Public Property WriteSeconds As Double
        Public Property ScanSeconds As Double
        Public Property RandomReadMs As Double
        Public Property SpliceSeconds As Double
        Public Property MergeSeconds As Double

        Public ReadOnly Property WriteMBps As Double
            Get
                Return If(WriteSeconds <= 0, 0.0, Bytes / 1024.0 / 1024.0 / WriteSeconds)
            End Get
        End Property

        Public ReadOnly Property RowsPerSecond As Double
            Get
                Return If(WriteSeconds <= 0, 0.0, DataRows / WriteSeconds)
            End Get
        End Property
    End Class

    Public Function Run(args As String()) As Integer
        Dim sizeGb As Double = 2.0
        Dim explicitRows As Long = 0
        Dim formats As String = "both"
        Dim sqlRows As Integer = 20000
        Dim cleanup As Boolean = False

        For i As Integer = 0 To args.Length - 1
            Select Case args(i)
                Case "--size"
                    If i + 1 < args.Length Then Double.TryParse(args(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, sizeGb)
                Case "--rows"
                    If i + 1 < args.Length Then Long.TryParse(args(i + 1), explicitRows)
                Case "--format"
                    If i + 1 < args.Length Then formats = args(i + 1).Trim().ToLowerInvariant()
                Case "--sql-rows"
                    If i + 1 < args.Length Then Integer.TryParse(args(i + 1), sqlRows)
                Case "--cleanup"
                    cleanup = True
            End Select
        Next

        Dim root As String = Path.Combine(Path.GetTempPath(), "jsql-stress-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)

        Console.WriteLine("==== JSql storage stress test ====")
        Console.WriteLine("work dir: " & root)
        Console.WriteLine($"target: {If(explicitRows > 0, explicitRows.ToString("N0") & " rows", sizeGb.ToString("0.##") & " GB")}")
        Console.WriteLine()

        Dim targetBytes As Long = CLng(sizeGb * 1024.0 * 1024.0 * 1024.0)
        Dim maxRows As Long = If(explicitRows > 0, explicitRows, CLng(targetBytes / 100L) + 1000000L)

        Dim wantJsonl As Boolean = formats = "both" OrElse formats = "jsonl"
        Dim wantCsv As Boolean = formats = "both" OrElse formats = "csv"

        If Not wantJsonl AndAlso Not wantCsv Then
            Console.WriteLine("  [FAIL] unknown --format value: " & formats & " (expected jsonl | csv | both)")
            Return 1
        End If

        ' 低于 1GB 的规模视为冒烟测试，不做磁盘空间门槛
        Dim free As Long = FreeSpace(root)

        If explicitRows = 0 AndAlso sizeGb >= 1.0 AndAlso free > 0 AndAlso free < targetBytes * 2L + 512L * 1024 * 1024 Then
            Console.WriteLine($"  [SKIP] not enough free disk space: free={free / 1024.0 / 1024.0 / 1024.0:0.##} GB, " &
                              $"need≈{sizeGb * 2.0 + 0.5:0.##} GB (bulk file + full rewrite temp)")
            Return 1
        End If

        Dim failures As Integer = 0
        Dim results As New List(Of BenchResult)

        Try
            If wantJsonl Then results.Add(RunBulk(root, StorageFormat.Jsonl, "JSONL", targetBytes, explicitRows, maxRows, failures))
            If wantCsv Then results.Add(RunBulk(root, StorageFormat.Csv, "CSV", targetBytes, explicitRows, maxRows, failures))

            Console.WriteLine()
            PrintReport(results)

            Console.WriteLine()
            failures += RunSqlBenchmark(root, sqlRows, wantJsonl, wantCsv)
        Finally
            If cleanup Then
                Try : Directory.Delete(root, recursive:=True) : Catch : End Try
                Console.WriteLine("  cleanup: removed " & root)
            Else
                Console.WriteLine("  work dir kept: " & root & "  (use --cleanup to remove)")
            End If
        End Try

        Console.WriteLine()
        Console.WriteLine($"  stress failures: {failures}")
        Console.WriteLine()

        Return If(failures = 0, 0, 1)
    End Function

#Region "批量写入 / 读取基准"

    Private Function RunBulk(root As String, format As StorageFormat, label As String, targetBytes As Long,
                             explicitRows As Long, maxRows As Long, ByRef failures As Integer) As BenchResult

        Console.WriteLine("-- bulk benchmark: " & label & " --")

        Dim dir As String = Path.Combine(root, "bulk-" & label)
        Directory.CreateDirectory(dir)

        Dim schema As TableSchema = StressSchema("t")
        Dim schemaPath As String = StorageLayout.SchemaPath(dir, "t")
        Dim dataPath As String = StorageLayout.DataPathForFormat(dir, "t", format)

        SchemaStore.Write(schemaPath, schema)

        Dim codec As IRowCodec = StressCodec(format)
        Dim options As New StorageOptions With {.Format = format, .MergeIdleSeconds = 0, .FsyncEachWrite = False}
        Dim storeOptions As TextStoreOptions = options.CreateStoreOptions()

        Dim result As New BenchResult With {.Label = label}
        Dim dataRows As Long = 0
        Dim headerLines As Long = If(codec.HasHeader, 1L, 0L)

        ' ---------- 批量写入（分块 append + 周期 merge，内存占用有界） ----------
        Dim store As New TextLineStore(dataPath, storeOptions)
        store.Open()

        Dim watch As Stopwatch = Stopwatch.StartNew()

        Try
            If codec.HasHeader Then
                store.AppendLine(codec.BuildHeader(schema))
            End If

            Const batchSize As Integer = 20000
            Const mergeEvery As Long = 100000L

            Dim pending As Long = 0
            Dim id As Long = 0

            Do
                Dim batch As New List(Of String)(batchSize)

                For k As Integer = 1 To batchSize
                    id += 1
                    batch.Add(codec.SerializeLine(StressRow(id), schema))
                Next

                store.AppendLines(batch)
                dataRows += batch.Count
                pending += batch.Count

                If pending >= mergeEvery Then
                    store.Merge()
                    pending = 0
                End If

                If dataRows >= maxRows Then
                    Exit Do
                End If

                If explicitRows = 0 AndAlso New FileInfo(dataPath).Length >= targetBytes Then
                    Exit Do
                End If
            Loop

            store.Merge()
        Finally
            store.Dispose()
        End Try

        watch.Stop()
        result.DataRows = dataRows
        result.Bytes = New FileInfo(dataPath).Length
        result.WriteSeconds = watch.Elapsed.TotalSeconds

        Console.WriteLine($"  write: {dataRows:N0} rows, {result.Bytes / 1024.0 / 1024.0 / 1024.0:0.000} GB, " &
                          $"{result.WriteSeconds:0.0}s, {result.WriteMBps:0.0} MB/s, {result.RowsPerSecond:N0} rows/s")

        ' ---------- 顺序扫描 ----------
        Dim scanned As Long = 0
        Dim scanWatch As Stopwatch

        Using scanner As New TextLineStore(dataPath, storeOptions)
            scanner.Open()
            scanWatch = Stopwatch.StartNew()

            For Each line As String In scanner.ReadLines()
                scanned += 1
            Next

            scanWatch.Stop()
            result.ScanSeconds = scanWatch.Elapsed.TotalSeconds
        End Using

        Console.WriteLine($"  scan: {scanned:N0} lines in {result.ScanSeconds:0.0}s")

        If scanned <> dataRows + headerLines Then
            failures += 1
            Console.WriteLine($"  [FAIL] {label}: scan line count expected={dataRows + headerLines:N0} actual={scanned:N0}")
        End If

        ' ---------- 随机读取 ----------
        Using reader As New TextLineStore(dataPath, storeOptions)
            reader.Open()

            Dim total As Long = reader.TotalLines
            Dim rnd As New Random(12345)
            Dim probes As Integer = 2000
            Dim rndWatch As Stopwatch = Stopwatch.StartNew()

            For i As Integer = 1 To probes
                Dim n As Long = CLng(Math.Floor(rnd.NextDouble() * total)) + 1L
                reader.ReadLine(n)
            Next

            rndWatch.Stop()
            result.RandomReadMs = rndWatch.Elapsed.TotalMilliseconds / probes
        End Using

        Console.WriteLine($"  random read: {result.RandomReadMs:0.000} ms/read")

        ' ---------- 局部改写 + merge（增量写） ----------
        Using writer As New TextLineStore(dataPath, storeOptions)
            writer.Open()

            Dim total As Long = writer.TotalLines
            Dim rnd As New Random(54321)
            Dim changes As Integer = 1000
            Dim spliceWatch As Stopwatch = Stopwatch.StartNew()

            For i As Integer = 1 To changes
                Dim low As Long = If(codec.HasHeader, 2L, 1L)
                Dim n As Long = low + CLng(Math.Floor(rnd.NextDouble() * Math.Max(1L, total - low)))
                writer.ReplaceLine(n, codec.SerializeLine(StressRow(900000000L + i), schema))
            Next

            spliceWatch.Stop()
            result.SpliceSeconds = spliceWatch.Elapsed.TotalSeconds

            Dim mergeWatch As Stopwatch = Stopwatch.StartNew()
            writer.Merge()
            mergeWatch.Stop()
            result.MergeSeconds = mergeWatch.Elapsed.TotalSeconds
        End Using

        Console.WriteLine($"  incremental: {result.SpliceSeconds:0.000}s for 1000 splices, merge {result.MergeSeconds:0.0}s")

        ' ---------- 重开后校验行数 ----------
        Using verify As New TextLineStore(dataPath, storeOptions)
            verify.Open()

            If verify.TotalLines <> dataRows + headerLines Then
                failures += 1
                Console.WriteLine($"  [FAIL] {label}: reopen line count expected={dataRows + headerLines:N0} actual={verify.TotalLines:N0}")
            Else
                Console.WriteLine($"  reopen verify: {verify.TotalLines:N0} lines OK")
            End If
        End Using

        If Not File.Exists(dataPath & ".idx") Then
            failures += 1
            Console.WriteLine($"  [FAIL] {label}: sparse index file not found")
        End If

        Console.WriteLine()
        Return result
    End Function

#End Region

#Region "SQL 级基准（可配置规模）"

    Private Function RunSqlBenchmark(root As String, sqlRows As Integer, wantJsonl As Boolean, wantCsv As Boolean) As Integer
        Console.WriteLine("-- sql level benchmark (bounded dataset) --")

        Dim failures As Integer = 0

        If sqlRows <= 0 Then
            Console.WriteLine("  skipped: --sql-rows <= 0")
            Return 0
        End If

        If wantJsonl Then failures += RunSqlFormat(root, StorageFormat.Jsonl, "JSONL", sqlRows)
        If wantCsv Then failures += RunSqlFormat(root, StorageFormat.Csv, "CSV", sqlRows)

        Return failures
    End Function

    Private Function RunSqlFormat(root As String, format As StorageFormat, label As String, sqlRows As Integer) As Integer
        Dim options As New StorageOptions With {.Format = format, .MergeIdleSeconds = 0, .FsyncEachWrite = False}
        Dim engine As New SqlEngine(root, options)
        Dim db As String = "sql_" & label.ToLowerInvariant()
        Dim failures As Integer = 0

        Try
            engine.ExecuteBatch(
                $"CREATE DATABASE {db};" &
                $"USE {db};" &
                "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, name VARCHAR(64), amount DOUBLE);")

            ' 分批 INSERT：每批 2000 行构成一条 INSERT 语句
            Dim insertWatch As Stopwatch = Stopwatch.StartNew()
            Const chunk As Integer = 2000
            Dim id As Long = 0

            Do While id < sqlRows
                Dim sb As New StringBuilder()
                sb.Append("INSERT INTO t (id, name, amount) VALUES ")

                For k As Integer = 1 To Math.Min(chunk, sqlRows - CInt(id))
                    If k > 1 Then sb.Append(",")
                    id += 1
                    sb.Append("(").Append(id.ToString(CultureInfo.InvariantCulture))
                    sb.Append(",'name_").Append(id.ToString(CultureInfo.InvariantCulture)).Append("',")
                    sb.Append((CDbl(id) * 1.25).ToString(CultureInfo.InvariantCulture)).Append(")")
                Next

                engine.Execute(sb.ToString())
            Loop

            insertWatch.Stop()

            Dim countWatch As Stopwatch = Stopwatch.StartNew()
            Dim inserted As Integer = CInt(CLng(engine.Execute("SELECT COUNT(*) FROM t").Rows(0)(0)))
            countWatch.Stop()

            If inserted <> sqlRows Then
                failures += 1
                Console.WriteLine($"  [FAIL] {label}: inserted expected={sqlRows} actual={inserted}")
            End If

            Dim updateWatch As Stopwatch = Stopwatch.StartNew()
            engine.Execute("UPDATE t SET amount = amount + 1 WHERE id <= 100")
            updateWatch.Stop()

            Dim deleteWatch As Stopwatch = Stopwatch.StartNew()
            engine.Execute("DELETE FROM t WHERE id > " & (sqlRows - 100).ToString(CultureInfo.InvariantCulture))
            deleteWatch.Stop()

            Dim final As Integer = CInt(CLng(engine.Execute("SELECT COUNT(*) FROM t").Rows(0)(0)))

            If final <> sqlRows - 100 Then
                failures += 1
                Console.WriteLine($"  [FAIL] {label}: final count expected={sqlRows - 100} actual={final}")
            End If

            Console.WriteLine($"  {label,-6} rows={sqlRows,8:N0}  insert={insertWatch.Elapsed.TotalSeconds,7:0.00}s  " &
                              $"count={countWatch.Elapsed.TotalSeconds,6:0.000}s  " &
                              $"update(100)={updateWatch.Elapsed.TotalSeconds,6:0.000}s  " &
                              $"delete(100)={deleteWatch.Elapsed.TotalSeconds,6:0.000}s  final={final:N0}")
        Finally
            engine.Dispose()
        End Try

        Return failures
    End Function

#End Region

#Region "helpers"

    Private Sub PrintReport(results As List(Of BenchResult))
        Console.WriteLine("-- benchmark summary --")

        If results.Count = 0 Then
            Console.WriteLine("  (no result)")
            Return
        End If

        Console.WriteLine($"  {"format",-7} {"rows",14} {"file(GB)",10} {"write(s)",10} {"MB/s",9} {"rows/s",13} {"scan(s)",9} {"rand(ms)",9} {"splice(s)",10} {"merge(s)",9}")

        For Each r In results
            Console.WriteLine($"  {r.Label,-7} {r.DataRows,14:N0} {r.Bytes / 1024.0 / 1024.0 / 1024.0,10:0.000} " &
                              $"{r.WriteSeconds,10:0.0} {r.WriteMBps,9:0.0} {r.RowsPerSecond,13:N0} " &
                              $"{r.ScanSeconds,9:0.0} {r.RandomReadMs,9:0.000} {r.SpliceSeconds,10:0.0} {r.MergeSeconds,9:0.0}")
        Next
    End Sub

    Private Function StressCodec(format As StorageFormat) As IRowCodec
        If format = StorageFormat.Csv Then
            Return New CsvRowCodec()
        End If

        Return New JsonRowCodec()
    End Function

    Private Function StressSchema(table As String) As TableSchema
        Dim schema As New TableSchema With {.TableName = table}
        schema.Columns.Add(New ColumnDef With {.Name = "id", .TypeName = "INT", .RawType = "INT", .PrimaryKey = True, .NotNull = True})
        schema.Columns.Add(New ColumnDef With {.Name = "name", .TypeName = "VARCHAR", .RawType = "VARCHAR(64)"})
        schema.Columns.Add(New ColumnDef With {.Name = "payload", .TypeName = "VARCHAR", .RawType = "VARCHAR(200)"})
        schema.Columns.Add(New ColumnDef With {.Name = "amount", .TypeName = "DOUBLE", .RawType = "DOUBLE"})
        schema.Columns.Add(New ColumnDef With {.Name = "ts", .TypeName = "DATETIME", .RawType = "DATETIME"})
        Return schema
    End Function

    Private Function StressRow(id As Long) As Dictionary(Of String, Object)
        Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        row("id") = id
        row("name") = "name_" & id.ToString("D10", CultureInfo.InvariantCulture)
        row("payload") = New String("x"c, PayloadSize)
        row("amount") = CDbl(id Mod 100000L) * 1.25
        row("ts") = "2026-09-11 10:00:00"
        Return row
    End Function

    Private Function FreeSpace(dirPath As String) As Long
        Try
            Dim drive As String = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(dirPath))
            Return New DriveInfo(drive).AvailableFreeSpace
        Catch
            Return -1
        End Try
    End Function

#End Region

End Module
