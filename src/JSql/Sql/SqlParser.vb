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

        Private Function IsClauseStart(t As Token) As Boolean
            Return t.IsSymbol(";") OrElse t.IsSymbol(",") OrElse t.IsKeyword("FROM") OrElse
                   t.IsKeyword("WHERE") OrElse t.IsKeyword("JOIN") OrElse t.IsKeyword("INNER") OrElse
                   t.IsKeyword("LEFT") OrElse t.IsKeyword("RIGHT") OrElse t.IsKeyword("CROSS") OrElse
                   t.IsKeyword("GROUP") OrElse t.IsKeyword("ORDER") OrElse t.IsKeyword("LIMIT") OrElse
                   t.IsKeyword("HAVING") OrElse t.IsKeyword("UNION") OrElse t.IsKeyword("ON") OrElse
                   t.IsKeyword("AS") OrElse t.Kind = TokenKind.EndOfFile
        End Function

        Private Function ParseSelect() As SelectStatement
            ExpectKeyword("SELECT")

            Dim stmt As New SelectStatement

            If AcceptKeyword("DISTINCT") Then
                stmt.Distinct = True
            Else
                AcceptKeyword("ALL")
            End If

            Do
                stmt.SelectItems.Add(ParseSelectItem())

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            If AcceptKeyword("FROM") Then
                stmt.FromTable = ExpectIdentifier()

                If AcceptKeyword("AS") Then
                    stmt.FromAlias = ExpectIdentifier()
                ElseIf tokens(p).Kind = TokenKind.Identifier AndAlso Not IsClauseStart(tokens(p)) Then
                    stmt.FromAlias = ExpectIdentifier()
                End If

                Do
                    Dim joinType As String = Nothing

                    If AcceptKeyword("INNER") OrElse AcceptKeyword("CROSS") Then
                        ExpectKeyword("JOIN")
                        joinType = "INNER"
                    ElseIf AcceptKeyword("LEFT") Then
                        AcceptKeyword("OUTER")
                        ExpectKeyword("JOIN")
                        joinType = "LEFT"
                    ElseIf AcceptKeyword("JOIN") Then
                        joinType = "INNER"
                    ElseIf AcceptSymbol(",") Then
                        joinType = "INNER"
                    Else
                        Exit Do
                    End If

                    Dim jc As New JoinClause With {.JoinType = joinType, .Table = ExpectIdentifier()}

                    If AcceptKeyword("AS") Then
                        jc.Alias = ExpectIdentifier()
                    ElseIf tokens(p).Kind = TokenKind.Identifier AndAlso Not tokens(p).IsKeyword("ON") Then
                        jc.Alias = ExpectIdentifier()
                    End If

                    If AcceptKeyword("ON") Then
                        jc.On = ParseExpression()
                    ElseIf joinType = "LEFT" Then
                        Throw Err("LEFT JOIN requires an ON condition!")
                    End If

                    stmt.Joins.Add(jc)
                Loop
            End If

            If AcceptKeyword("WHERE") Then
                stmt.Where = ParseExpression()
            End If

            If AcceptKeyword("GROUP") Then
                ExpectKeyword("BY")

                Do
                    stmt.GroupBy.Add(ParseExpression())

                    If Not AcceptSymbol(",") Then
                        Exit Do
                    End If
                Loop
            End If

            If AcceptKeyword("HAVING") Then
                stmt.Having = ParseExpression()
            End If

            stmt.OrderBy = ParseOrderBy()
            ParseLimit(stmt)

            Return stmt
        End Function

        Private Function ParseOrderBy() As List(Of OrderItem)
            Dim list As New List(Of OrderItem)

            If Not AcceptKeyword("ORDER") Then
                Return list
            End If

            ExpectKeyword("BY")

            Do
                Dim item As New OrderItem With {.Expression = ParseExpression()}

                If AcceptKeyword("DESC") Then
                    item.Descending = True
                Else
                    AcceptKeyword("ASC")
                End If

                list.Add(item)

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            Return list
        End Function

        Private Sub ParseLimit(stmt As SelectStatement)
            If Not AcceptKeyword("LIMIT") Then
                Return
            End If

            Dim first As Long = ParseIntLiteral("LIMIT")

            If AcceptSymbol(",") Then
                stmt.Offset = CInt(Math.Max(0L, first))
                stmt.Limit = CInt(ParseIntLiteral("LIMIT"))
            Else
                stmt.Limit = CInt(Math.Max(0L, first))

                If AcceptKeyword("OFFSET") Then
                    stmt.Offset = CInt(Math.Max(0L, ParseIntLiteral("OFFSET")))
                End If
            End If
        End Sub

        Private Function ParseIntLiteral(what As String) As Long
            Dim t As Token = tokens(p)

            If t.Kind = TokenKind.Number AndAlso Not t.Text.Contains(".") Then
                p += 1
                Return CLng(t.Text)
            End If

            Throw New SqlError("expected an integer literal for " & what & "!", t.Position)
        End Function

        Private Function ParseSelectItem() As SelectItem
            Dim item As New SelectItem
            Dim t As Token = tokens(p)

            If t.IsSymbol("*") Then
                p += 1
                item.Expression = New StarExpression
            ElseIf t.Kind = TokenKind.Identifier AndAlso Not t.IsKeyword("FROM") AndAlso
                   tokens(p + 1).IsSymbol(".") AndAlso tokens(p + 2).IsSymbol("*") Then
                Dim qualifier As String = ExpectIdentifier()
                ExpectSymbol(".")
                ExpectSymbol("*")
                item.Expression = New StarExpression With {.Qualifier = qualifier}
            Else
                item.Expression = ParseExpression()
            End If

            If AcceptKeyword("AS") Then
                item.Alias = ExpectIdentifier()
            ElseIf tokens(p).Kind = TokenKind.Identifier AndAlso Not IsClauseStart(tokens(p)) Then
                ' mysql also allows a trailing alias without the AS keyword
                item.Alias = ExpectIdentifier()
            End If

            Return item
        End Function

        Public Function ParseExpression() As Expression
            Return ParseOr()
        End Function

        Private Function ParseOr() As Expression
            Dim left As Expression = ParseAnd()

            Do While tokens(p).IsKeyword("OR")
                p += 1
                left = New BinaryExpression With {
                    .Op = BinaryOp.Or,
                    .Left = left,
                    .Right = ParseAnd()
                }
            Loop

            Return left
        End Function

        Private Function ParseAnd() As Expression
            Dim left As Expression = ParseNot()

            Do While tokens(p).IsKeyword("AND")
                p += 1
                left = New BinaryExpression With {
                    .Op = BinaryOp.And,
                    .Left = left,
                    .Right = ParseNot()
                }
            Loop

            Return left
        End Function

        Private Function ParseNot() As Expression
            If tokens(p).IsKeyword("NOT") Then
                p += 1

                ' NOT IN / NOT LIKE / NOT BETWEEN are handled in the comparison level
                Dim operand As Expression = ParseNot()

                If TypeOf operand Is InExpression OrElse
                   TypeOf operand Is LikeExpression OrElse
                   TypeOf operand Is BetweenExpression Then
                    Throw New SqlError("use 'expr NOT IN/BETWEEN/LIKE' syntax instead!", tokens(p).Position)
                End If

                Return New UnaryExpression With {.Op = "NOT", .Operand = operand}
            End If

            Return ParseComparison()
        End Function

        Private Function ParseComparison() As Expression
            Dim left As Expression = ParseAdditive()

            Do
                Dim t As Token = tokens(p)
                Dim op As BinaryOp? = Nothing

                If t.Kind = TokenKind.Symbol Then
                    Select Case t.Text
                        Case "=" : op = BinaryOp.Eq
                        Case "<>", "!=" : op = BinaryOp.Ne
                        Case "<" : op = BinaryOp.Lt
                        Case "<=" : op = BinaryOp.Le
                        Case ">" : op = BinaryOp.Gt
                        Case ">=" : op = BinaryOp.Ge
                    End Select
                End If

                If op Is Nothing Then
                    Exit Do
                End If

                p += 1

                left = New BinaryExpression With {.Op = op.Value, .Left = left, .Right = ParseAdditive()}
            Loop

            Return ParsePredicateSuffix(left)
        End Function

        Private Function ParsePredicateSuffix(left As Expression) As Expression
            Dim t As Token = tokens(p)
            Dim negated As Boolean = False

            ' MySQL supports a leading NOT: IS NOT NULL, NOT IN, NOT BETWEEN, NOT LIKE
            If t.IsKeyword("IS") Then
                p += 1

                Dim neg As Boolean = AcceptKeyword("NOT")
                ExpectKeyword("NULL")

                Return New IsNullExpression With {.Operand = left, .Negated = neg}
            End If

            If t.IsKeyword("IN") Then
                p += 1
                Return New InExpression With {.Operand = left, .Values = ParseExpressionList(), .Negated = False}
            End If

            If t.IsKeyword("LIKE") Then
                p += 1
                Return New LikeExpression With {.Operand = left, .Pattern = ParseAdditive(), .Negated = False}
            End If

            If t.IsKeyword("BETWEEN") Then
                p += 1

                Dim low As Expression = ParseAdditive()
                ExpectKeyword("AND")

                Return New BetweenExpression With {.Operand = left, .Low = low, .High = ParseAdditive(), .Negated = False}
            End If

            If t.IsKeyword("NOT") AndAlso
               (tokens(p + 1).IsKeyword("IN") OrElse tokens(p + 1).IsKeyword("LIKE") OrElse tokens(p + 1).IsKeyword("BETWEEN")) Then
                p += 1
                negated = True

                If tokens(p).IsKeyword("IN") Then
                    p += 1
                    Return New InExpression With {.Operand = left, .Values = ParseExpressionList(), .Negated = True}
                ElseIf tokens(p).IsKeyword("LIKE") Then
                    p += 1
                    Return New LikeExpression With {.Operand = left, .Pattern = ParseAdditive(), .Negated = True}
                Else
                    p += 1

                    Dim low As Expression = ParseAdditive()
                    ExpectKeyword("AND")

                    Return New BetweenExpression With {.Operand = left, .Low = low, .High = ParseAdditive(), .Negated = True}
                End If
            End If

            Return left
        End Function

        ' ARITHMETIC PARSING

    End Class
End Namespace
