Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports JSql.Engine
Imports JSql.Sql

Module Program

    Private Function PickRoot(args As String()) As String
        For i As Integer = 0 To args.Length - 1
            Dim a As String = args(i)

            If a = "--db" OrElse a = "-d" Then
                If i + 1 < args.Length Then
                    Return args(i + 1)
                End If
            ElseIf a.StartsWith("--db=") Then
                Return a.Substring("--db=".Length)
            End If
        Next

        Dim env As String = Environment.GetEnvironmentVariable("JSQL_HOME")

        If env IsNot Nothing AndAlso env.Length > 0 Then
            Return env
        End If

        Return Path.Combine(Directory.GetCurrentDirectory(), "jsql-data")
    End Function

    Sub Main(args As String())
        Dim root As String = PickRoot(args)
        Dim engine As New SqlEngine(root)

        Console.OutputEncoding = New UTF8Encoding(False)
        Console.WriteLine("JSql 0.1 - an experimental sql engine over json files")
        Console.WriteLine("data root: " & root)
        Console.WriteLine("type 'help' for the meta commands, 'quit' to leave.")
        Console.WriteLine()

        If Not Directory.Exists(root) Then
            Directory.CreateDirectory(root)
        End If

        Dim buffer As New StringBuilder
        Dim prompt As String = "jsql> "

        Do
            Console.Write(prompt)

            Dim line As String = Console.ReadLine()

            If line Is Nothing Then
                Exit Do
            End If

            Dim trimmed As String = line.Trim()

            If buffer.Length = 0 Then
                If trimmed.Equals("quit", StringComparison.OrdinalIgnoreCase) OrElse
                   trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase) OrElse
                   trimmed = "\q" Then
                    Console.WriteLine("bye.")
                    Exit Do
                End If

                If trimmed.Equals("help", StringComparison.OrdinalIgnoreCase) OrElse trimmed = "?" Then
                    PrintHelp(engine)
                    Continue Do
                End If

                If trimmed.Equals("cls", StringComparison.OrdinalIgnoreCase) OrElse trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase) Then
                    Console.Clear()
                    Continue Do
                End If

                If trimmed.Length = 0 Then
                    Continue Do
                End If
            End If

            If buffer.Length > 0 Then
                buffer.Append(" "c)
            End If

            buffer.Append(line)

            Dim sql As String = buffer.ToString()

            If Not IsComplete(sql) Then
                prompt = "    -> "
                Continue Do
            End If

            RunStatement(engine, sql)
            buffer.Clear()
            prompt = "jsql> "
        Loop
    End Sub

    Private Function IsComplete(sql As String) As Boolean
        If SqlEngine.SplitStatements(sql).LastOrDefault().Trim().EndsWith(";") Then
            ' the trailing semicolon marks the end of one statement
            Return SqlEngine.SplitStatements(sql).Any(Function(s) s.Trim().EndsWith(";"))
        End If

        Return sql.Trim().EndsWith(";")
    End Function

    Private Sub RunStatement(engine As SqlEngine, sql As String)
        Try
            For Each statement As String In SqlEngine.SplitStatements(sql)
                If statement.Trim().Length = 0 Then
                    Continue For
                End If

                Dim result As ResultSet = engine.Execute(statement)

                If result.IsQuery Then
                    PrintTable(result)
                    Console.WriteLine(result.Rows.Count & " row(s) in set")
                Else
                    Console.WriteLine(result.Message)
                End If
            Next
        Catch ex As SqlError
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine("sql error: " & ex.Message)
            Console.ResetColor()
        Catch ex As Exception
            Console.ForegroundColor = ConsoleColor.Red
            Console.WriteLine("engine error: " & ex.Message)
            Console.ResetColor()
        End Try

        Console.WriteLine()
    End Sub
End Module

    Private Function CellText(v As Object) As String
        If v Is Nothing Then
            Return "NULL"
        End If

        If TypeOf v Is Boolean Then
            Return If(CBool(v), "1", "0")
        End If

        If TypeOf v Is Double Then
            Dim d As Double = CDbl(v)

            If d = Math.Floor(d) AndAlso Math.Abs(d) < 1.0E+15 Then
                Return d.ToString("0.######")
            End If

            Return d.ToString("0.######")
        End If

        Return Convert.ToString(v)
    End Function

    Private Sub PrintTable(result As ResultSet)
        Dim count As Integer = result.Columns.Count
        Dim widths(count - 1) As Integer
        Dim texts As New List(Of String())

        For i As Integer = 0 To count - 1
            widths(i) = result.Columns(i).Length
        Next

        For Each row In result.Rows
            Dim cells(count - 1) As String

            For i As Integer = 0 To count - 1
                Dim cell As String = CellText(If(i < row.Length, row(i), Nothing))
                cells(i) = cell

                If cell.Length > widths(i) Then
                    widths(i) = cell.Length
                End If
            Next

            texts.Add(cells)
        Next

        Dim line(widths.Length - 1) As String

        For i As Integer = 0 To widths.Length - 1
            line(i) = New String("-"c, widths(i) + 2)
        Next

        Console.WriteLine("+" & String.Join("+", line) & "+")

        Dim head As String = ""

        For i As Integer = 0 To count - 1
            head &= "| " & result.Columns(i).PadRight(widths(i)) & " "
        Next

        Console.WriteLine(head & "|")
        Console.WriteLine("+" & String.Join("+", line) & "+")

        For Each row In texts
            Dim text As String = ""

            For i As Integer = 0 To count - 1
                text &= "| " & row(i).PadRight(widths(i)) & " "
            Next

            Console.WriteLine(text & "|")
        Next

        Console.WriteLine("+" & String.Join("+", line) & "+")
    End Sub

    Private Sub PrintHelp(engine As SqlEngine)
        Console.WriteLine("supported statements:")
        Console.WriteLine("  CREATE DATABASE | TABLE | INDEX  (IF NOT EXISTS)")
        Console.WriteLine("  DROP DATABASE | TABLE | INDEX   (IF EXISTS)")
        Console.WriteLine("  INSERT INTO t [(cols)] VALUES (...), (...)")
        Console.WriteLine("  UPDATE t SET c = expr, ... [WHERE ...]")
        Console.WriteLine("  DELETE FROM t [WHERE ...]")
        Console.WriteLine("  SELECT [DISTINCT] cols FROM t [alias]")
        Console.WriteLine("         [INNER|LEFT JOIN t2 alias ON expr]")
        Console.WriteLine("         [WHERE ...] [GROUP BY ...] [HAVING ...]")
        Console.WriteLine("         [ORDER BY col [ASC|DESC]] [LIMIT n [OFFSET m]]")
        Console.WriteLine("meta commands:")
        Console.WriteLine("  USE db | SHOW DATABASES | SHOW TABLES [FROM db]")
        Console.WriteLine("  SHOW INDEXES FROM t | DESCRIBE t")
        Console.WriteLine("  CREATE INDEX idx ON t (col) [USING HASH|BTREE|FULLTEXT]")
        Console.WriteLine("  help | clear | quit")

        If engine IsNot Nothing AndAlso engine.Catalog.CurrentDatabase IsNot Nothing Then
            Console.WriteLine("current database: " & engine.Catalog.CurrentDatabase)
        End If
    End Sub
End Module
