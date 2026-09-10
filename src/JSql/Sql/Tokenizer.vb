Imports System.Collections.Generic

Namespace Sql

    ''' <summary>sql syntax error with character position info</summary>
    Public Class SqlError : Inherits Exception

        Public ReadOnly Property Position As Integer

        Sub New(message As String, Optional position As Integer = -1)
            MyBase.New(message)
            Me.Position = position
        End Sub
    End Class

    Public Enum TokenKind
        Identifier
        QuotedIdentifier
        Number
        [String]
        Symbol
        EndOfFile
    End Enum

    Public Class Token

        Public Property Kind As TokenKind
        Public Property Text As String
        Public Property Position As Integer

        ''' <summary>case-insensitive keyword test on plain identifiers</summary>
        Public Function IsKeyword(name As String) As Boolean
            Return Kind = TokenKind.Identifier AndAlso String.Equals(Text, name, StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function IsSymbol(sym As String) As Boolean
            Return Kind = TokenKind.Symbol AndAlso Text = sym
        End Function

        Public Overrides Function ToString() As String
            Return $"{Kind}:{Text}"
        End Function
    End Class

    ''' <summary>
    ''' hand-written sql lexer: identifiers (and back-quoted quoted identifiers),
    ''' numbers, single-quoted strings, operators and punctuation. also skips
    ''' '--' line comments and '/* */' block comments.
    ''' </summary>
    Public Class SqlTokenizer

        Private ReadOnly sql As String
        Private pos As Integer

        Sub New(sql As String)
            Me.sql = sql
        End Sub

        Public Function Tokenize() As List(Of Token)
            Dim tokens As New List(Of Token)

            Do
                SkipBlank()
                Dim start As Integer = pos

                If pos >= sql.Length Then
                    tokens.Add(New Token With {.Kind = TokenKind.EndOfFile, .Text = "", .Position = start})
                    Exit Do
                End If

                Dim c As Char = sql(pos)

                If Char.IsLetter(c) OrElse c = "_"c Then
                    tokens.Add(ReadIdentifier(start))
                ElseIf Char.IsDigit(c) OrElse (c = "."c AndAlso pos + 1 < sql.Length AndAlso Char.IsDigit(sql(pos + 1))) Then
                    tokens.Add(ReadNumber(start))
                ElseIf c = "'"c Then
                    tokens.Add(ReadString(start))
                ElseIf c = "`"c Then
                    tokens.Add(ReadQuotedIdentifier(start))
                Else
                    tokens.Add(ReadSymbol(start))
                End If
            Loop

            Return tokens
        End Function

        Private Sub SkipBlank()
            Do While pos < sql.Length
                Dim c As Char = sql(pos)

                If Char.IsWhiteSpace(c) Then
                    pos += 1
                ElseIf c = "-"c AndAlso pos + 1 < sql.Length AndAlso sql(pos + 1) = "-"c Then
                    Do While pos < sql.Length AndAlso sql(pos) <> ControlChars.Lf
                        pos += 1
                    Loop
                ElseIf c = "/"c AndAlso pos + 1 < sql.Length AndAlso sql(pos + 1) = "*"c Then
                    pos += 2
                    Do While pos + 1 < sql.Length AndAlso Not (sql(pos) = "*"c AndAlso sql(pos + 1) = "/"c)
                        pos += 1
                    Loop
                    pos = Math.Min(pos + 2, sql.Length)
                Else
                    Exit Do
                End If
            Loop
        End Sub

        Private Function ReadIdentifier(start As Integer) As Token
            Do While pos < sql.Length AndAlso (Char.IsLetterOrDigit(sql(pos)) OrElse sql(pos) = "_"c OrElse sql(pos) = "$"c)
                pos += 1
            Loop

            Return New Token With {.Kind = TokenKind.Identifier, .Text = sql.Substring(start, pos - start), .Position = start}
        End Function

        Private Function ReadQuotedIdentifier(start As Integer) As Token
            pos += 1
            Dim sb As New Text.StringBuilder

            Do While pos < sql.Length
                Dim c As Char = sql(pos)

                If c = "`"c Then
                    If pos + 1 < sql.Length AndAlso sql(pos + 1) = "`"c Then
                        sb.Append("`"c)
                        pos += 2
                    Else
                        pos += 1
                        Return New Token With {.Kind = TokenKind.QuotedIdentifier, .Text = sb.ToString, .Position = start}
                    End If
                Else
                    sb.Append(c)
                    pos += 1
                End If
            Loop

            Throw New SqlError("unterminated quoted identifier!", start)
        End Function

        Private Function ReadNumber(start As Integer) As Token
            Do While pos < sql.Length AndAlso Char.IsDigit(sql(pos))
                pos += 1
            Loop

            If pos < sql.Length AndAlso sql(pos) = "."c Then
                pos += 1

                Do While pos < sql.Length AndAlso Char.IsDigit(sql(pos))
                    pos += 1
                Loop
            End If

            ' optional exponent
            If pos < sql.Length AndAlso (sql(pos) = "e"c OrElse sql(pos) = "E"c) Then
                Dim save As Integer = pos
                pos += 1

                If pos < sql.Length AndAlso (sql(pos) = "+"c OrElse sql(pos) = "-"c) Then
                    pos += 1
                End If

                If pos < sql.Length AndAlso Char.IsDigit(sql(pos)) Then
                    Do While pos < sql.Length AndAlso Char.IsDigit(sql(pos))
                        pos += 1
                    Loop
                Else
                    pos = save
                End If
            End If

            Return New Token With {.Kind = TokenKind.Number, .Text = sql.Substring(start, pos - start), .Position = start}
        End Function

        Private Function ReadString(start As Integer) As Token
            pos += 1
            Dim sb As New Text.StringBuilder

            Do While pos < sql.Length
                Dim c As Char = sql(pos)

                If c = "'"c Then
                    If pos + 1 < sql.Length AndAlso sql(pos + 1) = "'"c Then
                        sb.Append("'"c)
                        pos += 2
                    Else
                        pos += 1
                        Return New Token With {.Kind = TokenKind.String, .Text = sb.ToString, .Position = start}
                    End If
                ElseIf c = "\"c AndAlso pos + 1 < sql.Length Then
                    ' mysql style backslash escapes
                    Dim e As Char = sql(pos + 1)
                    Select Case e
                        Case "n"c : sb.Append(ControlChars.Lf)
                        Case "t"c : sb.Append(ControlChars.Tab)
                        Case "r"c : sb.Append(ControlChars.Cr)
                        Case "0"c : sb.Append(ControlChars.NullChar)
                        Case Else : sb.Append(e)
                    End Select
                    pos += 2
                Else
                    sb.Append(c)
                    pos += 1
                End If
            Loop

            Throw New SqlError("unterminated string literal!", start)
        End Function

        Private Function ReadSymbol(start As Integer) As Token
            Dim two As String = If(pos + 1 < sql.Length, sql.Substring(pos, 2), "")

            Select Case two
                Case "<>", "!=", "<=", ">="
                    pos += 2
                    Return New Token With {.Kind = TokenKind.Symbol, .Text = two, .Position = start}
            End Select

            Dim c As Char = sql(pos)
            pos += 1

            Select Case c
                Case ","c, "("c, ")"c, "."c, ";"c, "="c, "<"c, ">"c, "*"c, "+"c, "-"c, "/"c, "%"c
                    Return New Token With {.Kind = TokenKind.Symbol, .Text = c.ToString, .Position = start}
                Case Else
                    Throw New SqlError($"unexpected character '{c}' in sql text!", start)
            End Select
        End Function
    End Class
End Namespace
