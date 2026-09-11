Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports JSql.Engine
Imports JSql.Sqlite
Imports JSql.Storage

''' <summary>
''' SQLite 后端端到端回归：一个数据库一个 .sqlite 文件的建库建表、增删改查、
''' 模式元数据与值类型往返、列索引、重开持久化、后端注入切换与删除清理。
''' </summary>
Module StorageSqliteTests

    Private passed As Integer
    Private failed As Integer

    Public Function Run() As Integer
        passed = 0
        failed = 0

        Dim root As String = Path.Combine(Path.GetTempPath(), "jsql-sqlite-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)

        Console.WriteLine("==== JSql sqlite backend tests ====")
        Console.WriteLine("work dir: " & root)
        Console.WriteLine()

        Try
            RunCrudSuite(root)
            RunMetadataAndTypes(root)
            RunIndexing(root)
            RunProviderSwitch(root)
            RunDropSuite(root)
        Finally
            Try : Directory.Delete(root, recursive:=True) : Catch : End Try
        End Try

        Console.WriteLine()
        Console.WriteLine($"  passed: {passed}, failed: {failed}")
        Console.WriteLine()

        Return If(failed = 0, 0, 1)
    End Function

#Region "DDL / DML / 持久化"

    Private Sub RunCrudSuite(root As String)
        Console.WriteLine("-- sqlite crud --")

        Dim options As New StorageOptions With {.MergeIdleSeconds = 0}
        Dim dbFile As String = Path.Combine(root, "shop.sqlite")

        Dim engine As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            engine.ExecuteBatch(
                "CREATE DATABASE shop;" &
                "USE shop;" &
                "CREATE TABLE orders (" &
                "  oid INT NOT NULL PRIMARY KEY," &
                "  customer VARCHAR(40) NOT NULL," &
                "  amount DOUBLE NOT NULL" &
                ") COMMENT='order records';")

            Check("sqlite: table created", engine.Catalog.TableExists("shop", "orders"))
            Check("sqlite: one database = one .sqlite file", File.Exists(dbFile), dbFile)
            Check("sqlite: file is a real sqlite database", HasSqliteMagic(dbFile))
            Check("sqlite: provider name", engine.DataStore.ProviderName = "sqlite")

            engine.ExecuteBatch(
                "INSERT INTO orders (oid, customer, amount) VALUES " &
                "(1, 'alice', 120.5)," &
                "(2, 'bob', 80.0)," &
                "(3, 'carol', 9.25);")

            Dim count As ResultSet = engine.Execute("SELECT COUNT(*) FROM orders")
            Check("sqlite: inserted 3 rows", CInt(CLng(count.Rows(0)(0))) = 3, "count=" & CStr(count.Rows(0)(0)))

            Dim filtered As ResultSet = engine.Execute("SELECT customer FROM orders WHERE amount > 50 ORDER BY oid")
            Check("sqlite: filtered query", filtered.Rows.Count = 2 AndAlso CStr(filtered.Rows(0)(0)) = "alice",
                  $"rows={filtered.Rows.Count}")

            engine.Execute("UPDATE orders SET amount = 121.5 WHERE oid = 1")

            Dim updated As ResultSet = engine.Execute("SELECT amount FROM orders WHERE oid = 1")
            Check("sqlite: update applied",
                  updated.Rows.Count = 1 AndAlso Math.Abs(CDbl(updated.Rows(0)(0)) - 121.5) < 0.0001)

            engine.Execute("DELETE FROM orders WHERE oid = 2")

            Dim afterDelete As ResultSet = engine.Execute("SELECT COUNT(*) FROM orders")
            Check("sqlite: delete applied", CInt(CLng(afterDelete.Rows(0)(0))) = 2)

            Dim session As ITableSession = engine.Catalog.TryGetSession("shop", "orders")
            Check("sqlite: session layout", session IsNot Nothing AndAlso session.Layout = "SQLITE")
            Check("sqlite: no write ahead log", session IsNot Nothing AndAlso session.WalFileSize = 0)

            Dim layout As String = Nothing
            Dim dataBytes As Long = 0

            For Each r In engine.Catalog.DescribeStorage("shop")
                If CStr(r(0)) = "orders" Then
                    layout = CStr(r(1))
                    dataBytes = CLng(r(5))
                End If
            Next

            Check("sqlite: SHOW STORAGE layout", layout = "SQLITE", "actual=" & If(layout, "<none>"))
            Check("sqlite: SHOW STORAGE data bytes", dataBytes > 0, "bytes=" & dataBytes)

            engine.Execute("CHECKPOINT TABLE orders")

            Dim session2 As ITableSession = engine.Catalog.TryGetSession("shop", "orders")
            Check("sqlite: checkpoint committed", session2 IsNot Nothing AndAlso Not session2.HasPendingChanges)

            ' 没有 INTEGER PRIMARY KEY 的表：由引擎自增 rowid，且允许重复值
            engine.Execute("CREATE TABLE logs (msg VARCHAR(50) NOT NULL, level INT)")
            engine.Execute("INSERT INTO logs (msg, level) VALUES ('a', 1), ('a', 2)")

            Dim logCount As ResultSet = engine.Execute("SELECT COUNT(*) FROM logs")
            Check("sqlite: table without int pk", CInt(CLng(logCount.Rows(0)(0))) = 2)
        Finally
            engine.Dispose()
        End Try

        ' 重开：数据必须跨进程持久化
        Dim reopened As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            Dim stored As StoredTable = reopened.Catalog.LoadTable("shop", "orders")
            Check("sqlite: rows persisted after reopen", stored.Rows.Count = 2, $"actual={stored.Rows.Count}")
            Check("sqlite: schema comment persisted", stored.Schema.Comment = "order records")

            Dim alice As Dictionary(Of String, Object) =
                stored.Rows.First(Function(r) CStr(r("customer")) = "alice")

            Check("sqlite: updated value persisted", Math.Abs(CDbl(alice("amount")) - 121.5) < 0.0001)

            Dim names As New List(Of String)

            For Each row In stored.Rows
                names.Add(CStr(row("customer")))
            Next

            Check("sqlite: deleted row gone after reopen", Not names.Contains("bob"))
        Finally
            reopened.Dispose()
        End Try
    End Sub

#End Region

#Region "模式元数据与值类型往返"

    Private Sub RunMetadataAndTypes(root As String)
        Console.WriteLine("-- sqlite metadata / value round trip --")

        Dim options As New StorageOptions With {.MergeIdleSeconds = 0}
        Dim engine As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            engine.ExecuteBatch(
                "CREATE DATABASE meta;" &
                "USE meta;" &
                "CREATE TABLE t (" &
                "  id INT NOT NULL PRIMARY KEY," &
                "  name VARCHAR(30) NOT NULL DEFAULT 'n/a' COMMENT 'the name'," &
                "  active BOOLEAN," &
                "  created DATETIME," &
                "  score DOUBLE" &
                ");" &
                "INSERT INTO t (id, name, active, created, score) VALUES " &
                "(1, 'ann', true, '2026-01-02 03:04:05', 9.5);")

            Dim describe As ResultSet = engine.Execute("DESCRIBE t")
            Check("sqlite: describe column count", describe.Rows.Count = 5, $"actual={describe.Rows.Count}")
            Check("sqlite: describe type", CStr(describe.Rows(1)(1)) = "VARCHAR", "actual=" & CStr(describe.Rows(1)(1)))
            Check("sqlite: describe default", CStr(describe.Rows(1)(4)) = "n/a", "actual=" & CStr(describe.Rows(1)(4)))
            Check("sqlite: describe comment", CStr(describe.Rows(1)(5)) = "the name", "actual=" & CStr(describe.Rows(1)(5)))

            Dim stored As StoredTable = engine.Catalog.LoadTable("meta", "t")
            Dim row As Dictionary(Of String, Object) = stored.Rows(0)

            Check("sqlite: int round trip", CLng(row("id")) = 1L)
            Check("sqlite: varchar round trip", CStr(row("name")) = "ann")
            Check("sqlite: boolean round trip", CBool(row("active")) = True)
            Check("sqlite: datetime round trip", CStr(row("created")) = "2026-01-02 03:04:05",
                  "actual=" & CStr(row("created")))
            Check("sqlite: double round trip", Math.Abs(CDbl(row("score")) - 9.5) < 0.0001)
            Check("sqlite: null round trip", row("active") IsNot Nothing)
        Finally
            engine.Dispose()
        End Try
    End Sub

#End Region

#Region "列索引"

    Private Sub RunIndexing(root As String)
        Console.WriteLine("-- sqlite column indexes --")

        Dim options As New StorageOptions With {.MergeIdleSeconds = 0}
        Dim engine As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            engine.ExecuteBatch(
                "CREATE DATABASE idx;" &
                "USE idx;" &
                "CREATE TABLE u (id INT NOT NULL PRIMARY KEY, name VARCHAR(30) NOT NULL, age INT);" &
                "INSERT INTO u (id, name, age) VALUES (1, 'a', 20), (2, 'b', 30), (3, 'c', 40);" &
                "CREATE INDEX idx_u_name ON u (name) USING HASH;" &
                "CREATE INDEX idx_u_age ON u (age) USING BTREE;")

            Dim byName As ResultSet = engine.Execute("SELECT name FROM u WHERE name = 'b'")
            Check("sqlite: hash index query", byName.Rows.Count = 1 AndAlso CStr(byName.Rows(0)(0)) = "b")

            Dim byAge As ResultSet = engine.Execute("SELECT id FROM u WHERE age >= 30 ORDER BY id")
            Check("sqlite: range index query", byAge.Rows.Count = 2, $"actual={byAge.Rows.Count}")

            Dim indexDir As String = Path.Combine(root, ".jsql", "idx", ".indexes")
            Dim indexFiles As String() = If(Directory.Exists(indexDir), Directory.GetFiles(indexDir, "*.idx"), New String() {})

            Check("sqlite: index files persisted", indexFiles.Length >= 2, String.Join(",", indexFiles))
        Finally
            engine.Dispose()
        End Try

        ' 重开：索引文件应能被恢复并继续用于查询
        Dim reopened As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            reopened.Execute("USE idx")

            Dim hit As ResultSet = reopened.Execute("SELECT name FROM u WHERE name = 'c'")
            Check("sqlite: index query after reopen", hit.Rows.Count = 1 AndAlso CStr(hit.Rows(0)(0)) = "c")
        Finally
            reopened.Dispose()
        End Try
    End Sub

#End Region

#Region "后端注入 / 切换"

    Private Sub RunProviderSwitch(root As String)
        Console.WriteLine("-- storage provider injection / switch --")

        Dim options As New StorageOptions With {.MergeIdleSeconds = 0}
        Dim engine As New SqlEngine(root, options)

        Try
            Check("switch: default backend is text", engine.DataStore.ProviderName = "text")

            engine.SetStorage(New SqliteStorage(root, options))
            Check("switch: backend switched to sqlite", engine.DataStore.ProviderName = "sqlite")

            engine.ExecuteBatch(
                "CREATE DATABASE p;" &
                "USE p;" &
                "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, v VARCHAR(10));" &
                "INSERT INTO t (id, v) VALUES (1, 'x');")

            Dim r As ResultSet = engine.Execute("SELECT v FROM t")
            Check("switch: sqlite backend usable after switch",
                  r.Rows.Count = 1 AndAlso CStr(r.Rows(0)(0)) = "x")
        Finally
            engine.Dispose()
        End Try
    End Sub

#End Region

#Region "删除清理"

    Private Sub RunDropSuite(root As String)
        Console.WriteLine("-- sqlite drop table / database --")

        Dim options As New StorageOptions With {.MergeIdleSeconds = 0}
        Dim engine As New SqlEngine(New SqliteStorage(root, options), options)

        Try
            engine.ExecuteBatch(
                "CREATE DATABASE d;" &
                "USE d;" &
                "CREATE TABLE t1 (id INT);" &
                "CREATE TABLE t2 (id INT);")

            Check("drop: two tables created", engine.Catalog.GetTables("d").Count = 2)

            engine.Execute("DROP TABLE t1")
            Check("drop: table removed", Not engine.Catalog.TableExists("d", "t1"))
            Check("drop: remaining table kept", engine.Catalog.TableExists("d", "t2"))

            engine.Execute("DROP DATABASE d")
            Check("drop: database file removed", Not File.Exists(Path.Combine(root, "d.sqlite")))
            Check("drop: database no longer listed", Not engine.Catalog.GetDatabases().Contains("d"))
        Finally
            engine.Dispose()
        End Try
    End Sub

#End Region

#Region "helpers"

    ''' <summary>SQLite 文件头 16 字节固定为 "SQLite format 3" + NUL，用于确认是真实 sqlite 文件。</summary>
    Private Function HasSqliteMagic(path As String) As Boolean
        If Not File.Exists(path) Then
            Return False
        End If

        Dim bytes As Byte() = File.ReadAllBytes(path)
        Dim length As Integer = Math.Min(16, bytes.Length)

        If length < 16 Then
            Return False
        End If

        Dim header As String = Encoding.ASCII.GetString(bytes, 0, 16)
        Return header = "SQLite format 3" & ChrW(0)
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
