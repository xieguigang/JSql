Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports JSql.Engine
Imports JSql.Storage
Imports Microsoft.VisualBasic.Data.Repository

''' <summary>
''' JSql 存储格式功能回归 demo：对 JSONL 与 CSV 两种格式执行同一套端到端用例
''' （DDL / DML / 结果集校验 / 特殊字符转义 / WAL / checkpoint / 重开恢复 / 格式共存）。
''' </summary>
Module StorageFormatDemoTests

    Private passed As Integer
    Private failed As Integer

    Public Function Run() As Integer
        passed = 0
        failed = 0

        Dim root As String = Path.Combine(Path.GetTempPath(), "jsql-format-demo-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)

        Console.WriteLine("==== JSql storage format demo (jsonl / csv) ====")
        Console.WriteLine("work dir: " & root)
        Console.WriteLine()

        Try
            RunFormatSuite(root, StorageFormat.Jsonl, "JSONL", "demo_jsonl")
            RunFormatSuite(root, StorageFormat.Csv, "CSV", "demo_csv")
            RunWalReplay(root, StorageFormat.Jsonl, "JSONL")
            RunWalReplay(root, StorageFormat.Csv, "CSV")
            RunCoexistence(root)
            RunCsvNewlineNormalization()
        Finally
            Try : Directory.Delete(root, recursive:=True) : Catch : End Try
        End Try

        Console.WriteLine()
        Console.WriteLine($"  passed: {passed}, failed: {failed}")
        Console.WriteLine()

        Return If(failed = 0, 0, 1)
    End Function

#Region "端到端：DDL / DML / 转义 / checkpoint / 重开"

    Private Sub RunFormatSuite(root As String, format As StorageFormat, label As String, dbName As String)
        Console.WriteLine("-- " & label & " format --")

        Dim options As New StorageOptions With {
            .Format = format,
            .MergeIdleSeconds = 0,
            .FsyncEachWrite = False
        }

        Dim engine As New SqlEngine(root, options)

        Try
            engine.ExecuteBatch(
                $"CREATE DATABASE {dbName};" &
                $"USE {dbName};" &
                "CREATE TABLE t (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  name VARCHAR(64) NOT NULL," &
                "  age INT," &
                "  score DOUBLE," &
                "  active BOOLEAN," &
                "  created DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP" &
                ");")

            Check(label & ": table created", engine.Catalog.TableExists(dbName, "t"))

            Dim layoutOfT As String = Nothing

            For Each r In engine.Catalog.DescribeStorage(dbName)
                If CStr(r(0)) = "t" Then layoutOfT = CStr(r(1))
            Next

            Check(label & ": SHOW STORAGE layout", layoutOfT = label, $"actual={layoutOfT}")

            ' 批量插入，包含带逗号与双引号的字符串
            engine.ExecuteBatch(
                "INSERT INTO t (id, name, age, score, active) VALUES " &
                "(1, 'alice', 30, 12.5, 1)," &
                "(2, 'bob', 25, 7.25, 0)," &
                "(3, 'carol', 35, 9.0, 1)," &
                "(4, 'a, b ""quoted""', 40, 3.5, 0);")

            Dim count As ResultSet = engine.Execute("SELECT COUNT(*) FROM t")
            Check(label & ": inserted 4 rows", CInt(CLng(count.Rows(0)(0))) = 4, "count=" & CStr(count.Rows(0)(0)))

            Dim filtered As ResultSet = engine.Execute("SELECT id, name FROM t WHERE age > 26 ORDER BY id")
            Check(label & ": filtered rows", filtered.Rows.Count = 3, $"actual={filtered.Rows.Count}")
            Check(label & ": order by id", filtered.Rows.Count = 3 AndAlso CStr(filtered.Rows(0)(1)) = "alice")

            Dim tricky As ResultSet = engine.Execute("SELECT name FROM t WHERE id = 4")
            Check(label & ": comma/quote value round trip",
                  tricky.Rows.Count = 1 AndAlso CStr(tricky.Rows(0)(0)) = "a, b ""quoted""",
                  "actual=" & If(tricky.Rows.Count = 1, CStr(tricky.Rows(0)(0)), "<no row>"))

            engine.Execute("UPDATE t SET score = 100.25 WHERE id = 1")
            Dim upd As ResultSet = engine.Execute("SELECT score FROM t WHERE id = 1")
            Check(label & ": update applied", Math.Abs(CDbl(upd.Rows(0)(0)) - 100.25) < 0.0001, "actual=" & CStr(upd.Rows(0)(0)))

            engine.Execute("DELETE FROM t WHERE id = 2")
            Dim afterDelete As ResultSet = engine.Execute("SELECT COUNT(*) FROM t")
            Check(label & ": delete applied", CInt(CLng(afterDelete.Rows(0)(0))) = 3, "actual=" & CStr(afterDelete.Rows(0)(0)))

            Dim session As ITableSession = engine.Catalog.TryGetSession(dbName, "t")
            Check(label & ": session layout", session IsNot Nothing AndAlso session.Layout = label)
            Check(label & ": pending wal changes before checkpoint",
                  session IsNot Nothing AndAlso session.PendingOperations > 0 AndAlso session.WalFileSize > 0)

            engine.Execute("CHECKPOINT TABLE t")

            Dim session2 As ITableSession = engine.Catalog.TryGetSession(dbName, "t")
            Check(label & ": wal cleared after checkpoint",
                  session2 IsNot Nothing AndAlso Not session2.HasPendingChanges AndAlso session2.PendingOperations = 0)

            If format = StorageFormat.Csv Then
                Dim csvPath As String = StorageLayout.CsvDataPath(engine.Catalog.DatabaseDir(dbName), "t")
                Check("CSV: data file exists", File.Exists(csvPath), csvPath)

                Dim physical As String() = File.ReadAllLines(csvPath)
                Check("CSV: header line matches schema column order",
                      physical.Length > 0 AndAlso physical(0) = "id,name,age,score,active,created",
                      "actual=" & If(physical.Length > 0, physical(0), "<empty>"))
                Check("CSV: physical line count = header + data rows", physical.Length = 4, $"actual={physical.Length}")
            End If
        Finally
            engine.Dispose()
        End Try

        ' 重开：数据必须跨进程持久化
        Dim reopened As New SqlEngine(root, options)

        Try
            Dim stored As StoredTable = reopened.Catalog.LoadTable(dbName, "t")
            Check(label & ": rows persisted after reopen", stored.Rows.Count = 3, $"actual={stored.Rows.Count}")

            Dim names As New List(Of String)

            For Each row In stored.Rows
                names.Add(CStr(row("name")))
            Next

            Check(label & ": deleted row gone after reopen", Not names.Contains("bob"))

            Dim alice As Dictionary(Of String, Object) =
                stored.Rows.First(Function(r) CStr(r("name")) = "alice")

            Check(label & ": updated value persisted", Math.Abs(CDbl(alice("score")) - 100.25) < 0.0001)
        Finally
            reopened.Dispose()
        End Try
    End Sub

#End Region

#Region "WAL 重放（不合并直接重开）"

    Private Sub RunWalReplay(root As String, format As StorageFormat, label As String)
        Dim dir As String = Path.Combine(root, "walreplay-" & label)
        Directory.CreateDirectory(dir)

        Dim schema As TableSchema = DemoSchema("t")
        Dim schemaPath As String = StorageLayout.SchemaPath(dir, "t")
        Dim dataPath As String = StorageLayout.DataPathForFormat(dir, "t", format)
        Dim options As New StorageOptions With {.Format = format, .MergeIdleSeconds = 0}

        Dim rows As New List(Of Dictionary(Of String, Object))

        For i As Integer = 1 To 5
            Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
            row("id") = CLng(i)
            row("name") = "row" & i
            row("age") = CLng(20 + i)
            row("score") = CDbl(i) * 1.5
            row("active") = (i Mod 2 = 0)
            row("created") = "2026-01-0" & i & " 00:00:00"
            rows.Add(row)
        Next

        Dim writer As ITableSession = NewSessionFor(format, schema, schemaPath, dataPath, options)
        writer.SaveSchema(schema)
        writer.SyncRows(rows)

        Dim walSize As Long = writer.WalFileSize

        ' 直接 Dispose 会话（不会做 checkpoint），模拟进程崩溃：修改只留在 WAL
        writer.Dispose()

        Check(label & ": wal holds pending records before crash", walSize > 0, $"wal={walSize}")
        Check(label & ": data file still empty after crash", New FileInfo(dataPath).Length = 0,
              $"len={New FileInfo(dataPath).Length}")

        ' 重开：WAL 重放后挂起修改完整可见
        Dim reader As ITableSession = NewSessionFor(format, schema, schemaPath, dataPath, options)
        Dim replayed As List(Of Dictionary(Of String, Object)) = reader.ReadRows()

        Check(label & ": wal replayed rows", replayed.Count = 5, $"actual={replayed.Count}")
        Check(label & ": wal replayed value", replayed.Count = 5 AndAlso CStr(replayed(4)("name")) = "row5")

        reader.Merge()
        reader.Dispose()

        Check(label & ": merged data file non-empty", New FileInfo(dataPath).Length > 0)
        Check(label & ": wal cleared after merge", New FileInfo(dataPath & ".wal").Length = 0)
    End Sub

#End Region

#Region "同一数据库内两种格式共存"

    Private Sub RunCoexistence(root As String)
        Console.WriteLine("-- format coexistence in one database --")

        Const db As String = "mixed"
        Dim dir As String = Path.Combine(root, db)

        ' 先用 CSV 默认格式创建一张表
        Dim csvOptions As New StorageOptions With {.Format = StorageFormat.Csv, .MergeIdleSeconds = 0}

        Using engine As New SqlEngine(root, csvOptions)
            engine.ExecuteBatch(
                $"CREATE DATABASE {db};" &
                $"USE {db};" &
                "CREATE TABLE csv_table (id INT, name VARCHAR(20));")
            engine.Execute("INSERT INTO csv_table (id, name) VALUES (1, 'a'), (2, 'b')")
        End Using

        ' 同一目录内，再用 JSONL 默认格式创建另一张表
        Dim jsonlOptions As New StorageOptions With {.Format = StorageFormat.Jsonl, .MergeIdleSeconds = 0}

        Using engine As New SqlEngine(root, jsonlOptions)
            engine.ExecuteBatch(
                $"USE {db};" &
                "CREATE TABLE jsonl_table (id INT, name VARCHAR(20));")
            engine.Execute("INSERT INTO jsonl_table (id, name) VALUES (10, 'x'), (20, 'y')")
        End Using

        Using engine As New SqlEngine(root, jsonlOptions)
            Dim tables As List(Of String) = engine.Catalog.GetTables(db)
            Check("mixed: both tables discovered",
                  tables.Contains("csv_table") AndAlso tables.Contains("jsonl_table"),
                  String.Join(",", tables))

            Dim csv As StoredTable = engine.Catalog.LoadTable(db, "csv_table")
            Dim jsonl As StoredTable = engine.Catalog.LoadTable(db, "jsonl_table")

            Check("mixed: csv table readable", csv.Rows.Count = 2 AndAlso CStr(csv.Rows(0)("name")) = "a")
            Check("mixed: jsonl table readable", jsonl.Rows.Count = 2 AndAlso CStr(jsonl.Rows(0)("name")) = "x")
            Check("mixed: csv data file exists", File.Exists(StorageLayout.CsvDataPath(dir, "csv_table")))
            Check("mixed: jsonl data file exists", File.Exists(StorageLayout.DataPath(dir, "jsonl_table")))
        End Using
    End Sub

#End Region

#Region "CSV 单元格换行规范化"

    Private Sub RunCsvNewlineNormalization()
        Console.WriteLine("-- csv cell newline normalization --")

        Dim codec As New CsvRowCodec()
        Dim schema As New TableSchema With {.TableName = "t"}
        schema.Columns.Add(New ColumnDef With {.Name = "id", .TypeName = "INT", .RawType = "INT"})
        schema.Columns.Add(New ColumnDef With {.Name = "note", .TypeName = "VARCHAR", .RawType = "VARCHAR(200)"})

        Dim row As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        row("id") = 1L
        row("note") = "line1" & vbLf & "line2" & vbCr & "end"

        Dim line As String = codec.SerializeLine(row, schema)
        Check("csv: no literal newline in the encoded line",
              line.IndexOf(vbLf) < 0 AndAlso line.IndexOf(vbCr) < 0,
              line)

        Dim back As Dictionary(Of String, Object) = codec.DeserializeLine(line, schema)
        Check("csv: normalized value round trip",
              CStr(back("note")) = "line1 line2 end",
              "actual=" & CStr(back("note")))
    End Sub

#End Region

#Region "helpers"

    Private Function NewSessionFor(format As StorageFormat, schema As TableSchema, schemaPath As String,
                                   dataPath As String, options As StorageOptions) As ITableSession
        If format = StorageFormat.Csv Then
            Return New CsvTableSession(schema, schemaPath, dataPath, options)
        End If

        Return New JsonlTableSession(schema, schemaPath, dataPath, options)
    End Function

    Private Function DemoSchema(table As String) As TableSchema
        Dim schema As New TableSchema With {.TableName = table}
        schema.Columns.Add(New ColumnDef With {.Name = "id", .TypeName = "INT", .RawType = "INT", .PrimaryKey = True, .NotNull = True})
        schema.Columns.Add(New ColumnDef With {.Name = "name", .TypeName = "VARCHAR", .RawType = "VARCHAR(64)", .NotNull = True})
        schema.Columns.Add(New ColumnDef With {.Name = "age", .TypeName = "INT", .RawType = "INT"})
        schema.Columns.Add(New ColumnDef With {.Name = "score", .TypeName = "DOUBLE", .RawType = "DOUBLE"})
        schema.Columns.Add(New ColumnDef With {.Name = "active", .TypeName = "BOOLEAN", .RawType = "BOOLEAN"})
        schema.Columns.Add(New ColumnDef With {.Name = "created", .TypeName = "DATETIME", .RawType = "DATETIME"})
        Return schema
    End Function

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
