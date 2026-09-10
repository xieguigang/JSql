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

        ' STATEMENT DISPATCH

    End Class
End Namespace
