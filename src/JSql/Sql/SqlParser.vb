Namespace Sql

    Public Class SqlParser

        Private ReadOnly tokens As List(Of Token)
        Private p As Integer

        Sub New(sql As String)
            tokens = New SqlTokenizer(sql).Tokenize()
            p = 0
        End Sub

        Private Function Current() As Token
            Return tokens(p)
        End Function

        Private Function AtEof() As Boolean
            Return tokens(p).Kind = TokenKind.EndOfFile
        End Function

        Private Function AcceptKeyword(name As String) As Boolean
            If tokens(p).IsKeyword(name) Then
                p += 1
                Return True
            End If

            Return False
        End Function

        Private Sub ExpectKeyword(name As String)
            If Not AcceptKeyword(name) Then
                Throw Err("expected keyword " & name & " but found '" & tokens(p).Text & "'!")
            End If
        End Sub

        Private Function AcceptSymbol(sym As String) As Boolean
            If tokens(p).IsSymbol(sym) Then
                p += 1
                Return True
            End If

            Return False
        End Function

        Private Sub ExpectSymbol(sym As String)
            If Not AcceptSymbol(sym) Then
                Throw Err("expected '" & sym & "' but found '" & tokens(p).Text & "'!")
            End If
        End Sub

        Private Function ExpectIdentifier() As String
            Dim t As Token = tokens(p)

            If t.Kind = TokenKind.Identifier OrElse t.Kind = TokenKind.QuotedIdentifier Then
                p += 1
                Return t.Text
            End If

            Throw Err("expected an identifier but found '" & t.Text & "'!")
        End Function

        Private Function Err(message As String) As SqlError
            Return New SqlError(message, tokens(p).Position)
        End Function

        ''' <summary>parses one statement; a trailing ';' is accepted and extra tokens rejected</summary>
        Public Function ParseStatement() As SqlStatement
            If AtEof() Then
                Throw Err("empty sql statement!")
            End If

            Dim stmt As SqlStatement = ParseStatementCore()

            AcceptSymbol(";")

            If Not AtEof() Then
                Throw Err("unexpected token '" & tokens(p).Text & "' after the end of statement!")
            End If

            Return stmt
        End Function

        Private Function ParseStatementCore() As SqlStatement
            Dim t As Token = tokens(p)

            If t.IsKeyword("SELECT") Then
                Return ParseSelect()
            ElseIf t.IsKeyword("INSERT") Then
                Return ParseInsert()
            ElseIf t.IsKeyword("UPDATE") Then
                Return ParseUpdate()
            ElseIf t.IsKeyword("DELETE") Then
                Return ParseDelete()
            ElseIf t.IsKeyword("CREATE") Then
                Return ParseCreate()
            ElseIf t.IsKeyword("DROP") Then
                Return ParseDrop()
            ElseIf t.IsKeyword("USE") Then
                p += 1
                Return New UseStatement With {.Database = ExpectIdentifier()}
            ElseIf t.IsKeyword("SHOW") Then
                Return ParseShow()
            ElseIf t.IsKeyword("DESCRIBE") OrElse t.IsKeyword("DESC") OrElse t.IsKeyword("EXPLAIN") Then
                p += 1
                Return New ShowStatement With {.Kind = ShowKind.Columns, .Target = ExpectIdentifier()}
            End If

            Throw Err("unsupported sql statement starts with '" & t.Text & "'!")
        End Function

        ' SELECT PARSING

    End Class
End Namespace
