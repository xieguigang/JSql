Imports JSql.Storage

Namespace Sql

    ''' <summary>
    ''' recursive-descent parser for the mysql-compatible sql subset supported by JSql
    ''' </summary>
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

        Private Function ParseAdditive() As Expression
            Dim left As Expression = ParseMultiplicative()

            Do
                Dim t As Token = tokens(p)
                Dim op As BinaryOp? = Nothing

                If t.IsSymbol("+") Then
                    op = BinaryOp.Add
                ElseIf t.IsSymbol("-") Then
                    op = BinaryOp.Subtract
                Else
                    Exit Do
                End If

                p += 1

                left = New BinaryExpression With {.Op = op.Value, .Left = left, .Right = ParseMultiplicative()}
            Loop

            Return left
        End Function

        Private Function ParseMultiplicative() As Expression
            Dim left As Expression = ParseUnary()

            Do
                Dim t As Token = tokens(p)
                Dim op As BinaryOp? = Nothing

                If t.IsSymbol("*") Then
                    op = BinaryOp.Multiply
                ElseIf t.IsSymbol("/") Then
                    op = BinaryOp.Divide
                ElseIf t.IsSymbol("%") Then
                    op = BinaryOp.Mod
                Else
                    Exit Do
                End If

                p += 1

                left = New BinaryExpression With {.Op = op.Value, .Left = left, .Right = ParseUnary()}
            Loop

            Return left
        End Function

        Private Function ParseUnary() As Expression
            Dim t As Token = tokens(p)

            If t.IsSymbol("-") Then
                p += 1
                Return New UnaryExpression With {.Op = "-", .Operand = ParseUnary()}
            ElseIf t.IsSymbol("+") Then
                p += 1
                Return ParseUnary()
            End If

            Return ParsePrimary()
        End Function

        Private Function ParsePrimary() As Expression
            Dim t As Token = tokens(p)

            If AcceptSymbol("(") Then
                Dim inner As Expression = ParseExpression()
                ExpectSymbol(")")
                Return inner
            End If

            Select Case t.Kind
                Case TokenKind.String
                    p += 1
                    Return New LiteralExpression(t.Text)

                Case TokenKind.Number
                    p += 1

                    If t.Text.Contains(".") OrElse t.Text.Contains("e") OrElse t.Text.Contains("E") Then
                        Return New LiteralExpression(CDbl(t.Text))
                    Else
                        Return New LiteralExpression(CLng(t.Text))
                    End If
            End Select

            If t.Kind = TokenKind.Identifier Then
                If t.IsKeyword("NULL") Then
                    p += 1
                    Return LiteralExpression.NullLiteral()
                ElseIf t.IsKeyword("TRUE") Then
                    p += 1
                    Return New LiteralExpression(True)
                ElseIf t.IsKeyword("FALSE") Then
                    p += 1
                    Return New LiteralExpression(False)
                ElseIf t.IsKeyword("CASE") OrElse t.IsKeyword("INTERVAL") OrElse t.IsKeyword("CAST") Then
                    Throw New SqlError("unsupported sql expression: " & t.Text, t.Position)
                End If

                ' aggregate function call
                If tokens(p + 1).IsSymbol("(") Then
                    Dim name As String = ExpectIdentifier()
                    ExpectSymbol("(")

                    Dim fn As New FunctionCallExpression With {.Name = name.ToUpper}

                    If AcceptSymbol("*") Then
                        fn.IsStar = True
                        ExpectSymbol(")")
                        Return fn
                    End If

                    Do
                        fn.Args.Add(ParseExpression())

                        If AcceptSymbol(",") Then
                            Continue Do
                        End If

                        Exit Do
                    Loop

                    ExpectSymbol(")")
                    Return fn
                End If
            End If

            ' column reference: [qualifier.]column
            If t.Kind = TokenKind.Identifier OrElse t.Kind = TokenKind.QuotedIdentifier Then
                Dim first As String = ExpectIdentifier()

                If AcceptSymbol(".") Then
                    Return New IdentifierExpression(first, ExpectIdentifier())
                End If

                Return New IdentifierExpression(first)
            End If

            Throw New SqlError("unexpected token '" & t.Text & "' in expression!", t.Position)
        End Function

        Private Function ParseExpressionList() As List(Of Expression)
            ExpectSymbol("(")

            Dim list As New List(Of Expression)

            Do
                list.Add(ParseExpression())

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            ExpectSymbol(")")
            Return list
        End Function

        Private Function ParseInsert() As InsertStatement
            ExpectKeyword("INSERT")

            Dim stmt As New InsertStatement

            AcceptKeyword("LOW_PRIORITY")
            AcceptKeyword("IGNORE")

            If AcceptKeyword("INTO") Then
                ' nothing to do, INTO is optional in mysql-ish dialects
            End If

            stmt.Table = ExpectIdentifier()

            If AcceptSymbol("(") Then
                stmt.Columns = New List(Of String)

                Do
                    stmt.Columns.Add(ExpectIdentifier())

                    If Not AcceptSymbol(",") Then
                        Exit Do
                    End If
                Loop

                ExpectSymbol(")")
            End If

            ExpectKeyword("VALUES")

            Do
                Dim row As New List(Of Expression)

                ExpectSymbol("(")

                Do
                    row.Add(ParseExpression())

                    If Not AcceptSymbol(",") Then
                        Exit Do
                    End If
                Loop

                ExpectSymbol(")")
                stmt.ValueRows.Add(row)

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            Return stmt
        End Function

        Private Function ParseUpdate() As UpdateStatement
            ExpectKeyword("UPDATE")

            Dim stmt As New UpdateStatement

            stmt.Table = ExpectIdentifier()

            If AcceptKeyword("AS") Then ExpectIdentifier()

            ExpectKeyword("SET")

            Do
                Dim a As New Assignment

                a.Column = ExpectIdentifier()
                ExpectSymbol("=")
                a.Value = ParseExpression()
                stmt.Assignments.Add(a)

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            If AcceptKeyword("WHERE") Then
                stmt.Where = ParseExpression()
            End If

            If AcceptKeyword("LIMIT") Then
                Throw New SqlError("UPDATE ... LIMIT is not supported yet!", tokens(p).Position)
            End If

            Return stmt
        End Function

        Private Function ParseDelete() As DeleteStatement
            ExpectKeyword("DELETE")
            ExpectKeyword("FROM")

            Dim stmt As New DeleteStatement

            stmt.Table = ExpectIdentifier()

            If AcceptKeyword("WHERE") Then
                stmt.Where = ParseExpression()
            End If

            Return stmt
        End Function

        Private Function ParseCreate() As CreateStatement
            ExpectKeyword("CREATE")

            Dim stmt As New CreateStatement

            If AcceptKeyword("TEMPORARY") Then
                ' ignored: everything stays file based
            End If

            If AcceptKeyword("DATABASE") OrElse AcceptKeyword("SCHEMA") Then
                stmt.Kind = CreateKind.Database
            ElseIf AcceptKeyword("TABLE") Then
                stmt.Kind = CreateKind.Table
            ElseIf AcceptKeyword("UNIQUE") Then
                ExpectKeyword("INDEX")
                stmt.Kind = CreateKind.Index
            ElseIf AcceptKeyword("INDEX") OrElse AcceptKeyword("KEY") Then
                stmt.Kind = CreateKind.Index
            Else
                Throw Err("unsupported CREATE object, only DATABASE/TABLE/INDEX are supported!")
            End If

            If AcceptKeyword("IF") Then
                ExpectKeyword("NOT")
                ExpectKeyword("EXISTS")
                stmt.IfNotExists = True
            End If

            stmt.Name = ExpectIdentifier()

            If stmt.Kind = CreateKind.Table Then
                stmt.Columns = ParseColumnDefinitions(stmt.Keys)
                ' ENGINE=..., CHARSET=... are ignored, COMMENT='...' is kept
                ParseTableOptions(stmt)
            ElseIf stmt.Kind = CreateKind.Index Then
                ExpectKeyword("ON")
                stmt.OnTable = ExpectIdentifier()
                ExpectSymbol("(")
                stmt.OnColumn = ExpectIdentifier()
                ExpectSymbol(")")

                If AcceptKeyword("USING") Then
                    stmt.IndexKind = ExpectIdentifier().ToUpper
                End If
            End If

            Return stmt
        End Function

        Private Function ParseColumnDefinitions(keys As List(Of TableKeyInfo)) As List(Of ColumnDef)
            ExpectSymbol("(")

            Dim cols As New List(Of ColumnDef)

            Do
                Dim t As Token = tokens(p)

                If t.IsKeyword("PRIMARY") OrElse t.IsKeyword("UNIQUE") OrElse
                   t.IsKeyword("KEY") OrElse t.IsKeyword("INDEX") Then
                    ' a table level key definition: PRIMARY KEY(cols) / UNIQUE KEY name(cols) / KEY name(cols)
                    keys.Add(ParseTableKey())
                ElseIf t.IsKeyword("CONSTRAINT") OrElse t.IsKeyword("FOREIGN") Then
                    Throw Err("named constraints and foreign keys are not supported yet!")
                Else
                    cols.Add(ParseColumnDef())
                End If

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            ExpectSymbol(")")

            If cols.Count = 0 Then
                Throw Err("empty column list in CREATE TABLE!")
            End If

            Return cols
        End Function

        ''' <summary>
        ''' parse PRIMARY KEY (cols) / [UNIQUE] KEY|INDEX [name] (cols) definitions.
        ''' </summary>
        Private Function ParseTableKey() As TableKeyInfo
            Dim key As New TableKeyInfo

            If AcceptKeyword("PRIMARY") Then
                ExpectKeyword("KEY")
                key.Primary = True
                key.Unique = True
                key.Name = "PRIMARY"
            Else
                If AcceptKeyword("UNIQUE") Then
                    key.Unique = True
                End If

                If Not (AcceptKeyword("KEY") OrElse AcceptKeyword("INDEX")) Then
                    Throw Err("expected a KEY or INDEX definition!")
                End If

                ' the key name is optional
                If tokens(p).Kind = TokenKind.Identifier OrElse tokens(p).Kind = TokenKind.QuotedIdentifier Then
                    If Not tokens(p + 1).IsSymbol("(") Then
                        Throw Err("expected '(' after the key name!")
                    End If

                    key.Name = ExpectIdentifier()
                End If
            End If

            ExpectSymbol("(")

            Do
                key.Columns.Add(ExpectIdentifier())

                If Not AcceptSymbol(",") Then
                    Exit Do
                End If
            Loop

            ExpectSymbol(")")

            ' optional index options: USING BTREE, COMMENT '...'
            Do While tokens(p).Kind = TokenKind.Identifier AndAlso Not IsClauseStart(tokens(p))
                If tokens(p).IsKeyword("COMMENT") Then
                    key.Name = If(key.Name, "")
                    p += 1

                    If tokens(p).Kind = TokenKind.String Then
                        p += 1
                    End If
                Else
                    p += 1

                    If AcceptSymbol("=") Then
                        p += 1
                    End If
                End If
            Loop

            If key.Name Is Nothing Then
                key.Name = "key_" & String.Join("_", key.Columns.ToArray())
            End If

            Return key
        End Function

        Private Function ParseColumnDef() As ColumnDef
            Dim col As New ColumnDef

            col.Name = ExpectIdentifier()

            Dim typeText As String = ExpectIdentifier()
            ' the bare type name, used to resolve the canonical sql type
            Dim baseType As String = typeText

            ' optional type arguments: VARCHAR(255), DECIMAL(10,2)
            If AcceptSymbol("(") Then
                typeText &= "("

                Do
                    typeText &= tokens(p).Text
                    p += 1

                    If Not AcceptSymbol(",") Then
                        Exit Do
                    End If

                    typeText &= ","
                Loop

                ExpectSymbol(")")
                typeText &= ")"
            End If

            ' keep the unsigned flag inside the original type text: int unsigned
            If AcceptKeyword("UNSIGNED") Then
                typeText &= " unsigned"
            End If

            AcceptKeyword("ZEROFILL")

            col.RawType = typeText

            Try
                col.TypeName = SqlTypes.NormalizeType(baseType)
            Catch ex As Exception
                Throw New SqlError("unsupported column type: " & typeText, tokens(p).Position)
            End Try

            Do
                If AcceptKeyword("NOT") Then
                    ExpectKeyword("NULL")
                    col.NotNull = True
                ElseIf AcceptKeyword("NULL") Then
                    col.NotNull = False
                ElseIf AcceptKeyword("PRIMARY") Then
                    ExpectKeyword("KEY")
                    col.PrimaryKey = True
                    col.NotNull = True
                ElseIf AcceptKeyword("UNIQUE") Then
                    ' ignored by this experimental engine
                ElseIf AcceptKeyword("AUTO_INCREMENT") OrElse AcceptKeyword("AUTOINCREMENT") Then
                    ' ignored: no auto value generator yet
                ElseIf AcceptKeyword("DEFAULT") Then
                    col.DefaultValue = ParseDefaultLiteral()
                ElseIf AcceptKeyword("COMMENT") Then
                    col.Comment = ExpectStringLiteral("column comment")
                ElseIf AcceptKeyword("ON") Then
                    ' ON UPDATE CURRENT_TIMESTAMP and friends are ignored
                    SkipUntilCommaOrEnd()
                Else
                    Exit Do
                End If
            Loop

            Return col
        End Function

        Private Function ParseDefaultLiteral() As Object
            Dim t As Token = tokens(p)

            Select Case t.Kind
                Case TokenKind.String
                    p += 1
                    Return t.Text
                Case TokenKind.Number
                    p += 1

                    If t.Text.Contains(".") Then
                        Return CDbl(t.Text)
                    Else
                        Return CLng(t.Text)
                    End If
            End Select

            If t.IsKeyword("NULL") Then
                p += 1
                Return Nothing
            ElseIf t.IsKeyword("TRUE") Then
                p += 1
                Return True
            ElseIf t.IsKeyword("FALSE") Then
                p += 1
                Return False
            ElseIf t.IsKeyword("CURRENT_TIMESTAMP") Then
                ' resolved to the current time when a row is inserted
                p += 1

                If AcceptSymbol("(") Then
                    ExpectSymbol(")")
                End If

                Return "CURRENT_TIMESTAMP"
            ElseIf t.IsKeyword("NOW") Then
                p += 1
                ExpectSymbol("(")
                ExpectSymbol(")")
                Return "CURRENT_TIMESTAMP"
            End If

            Throw New SqlError("unsupported DEFAULT value: " & t.Text, t.Position)
        End Function

        Private Function ExpectStringLiteral(what As String) As String
            Dim t As Token = tokens(p)

            If t.Kind <> TokenKind.String Then
                Throw New SqlError("expected a string literal for " & what & "!", t.Position)
            End If

            p += 1
            Return t.Text
        End Function

        ''' <summary>skip the remaining tokens of one column attribute</summary>
        Private Sub SkipUntilCommaOrEnd()
            Do
                Dim t As Token = tokens(p)

                If t.Kind = TokenKind.EndOfFile OrElse t.IsSymbol(",") OrElse t.IsSymbol(")") Then
                    Exit Do
                End If

                p += 1
            Loop
        End Sub

        ''' <summary>
        ''' parse the trailing table options: COMMENT='text' is kept as the table
        ''' comment, everything else (ENGINE=..., CHARSET=..., AUTO_INCREMENT=...)
        ''' is parsed away.
        ''' </summary>
        Private Sub ParseTableOptions(stmt As CreateStatement)
            Do While tokens(p).Kind = TokenKind.Identifier
                If IsClauseStart(tokens(p)) Then
                    Exit Do
                End If

                If tokens(p).IsKeyword("COMMENT") Then
                    p += 1
                    AcceptSymbol("=")
                    stmt.TableComment = ExpectStringLiteral("the table comment")
                Else
                    p += 1

                    If AcceptSymbol("=") Then
                        If tokens(p).Kind = TokenKind.Identifier OrElse
                           tokens(p).Kind = TokenKind.Number OrElse
                           tokens(p).Kind = TokenKind.String Then
                            p += 1
                        End If
                    End If
                End If
            Loop
        End Sub

        Private Sub SkipTrailingOptions()
            ' ENGINE=..., CHARSET=..., COMMENT=... are parsed away
            Do While tokens(p).Kind = TokenKind.Identifier
                If IsClauseStart(tokens(p)) Then
                    Exit Do
                End If

                p += 1

                If AcceptSymbol("=") Then
                    p += 1
                End If
            Loop
        End Sub

        Private Function ParseDrop() As DropStatement
            ExpectKeyword("DROP")

            Dim stmt As New DropStatement

            If AcceptKeyword("TEMPORARY") Then
                ' ignored
            End If

            If AcceptKeyword("TABLE") Then
                stmt.Kind = DropKind.Table
            ElseIf AcceptKeyword("INDEX") OrElse AcceptKeyword("KEY") Then
                stmt.Kind = DropKind.Index
            ElseIf AcceptKeyword("DATABASE") OrElse AcceptKeyword("SCHEMA") Then
                stmt.Kind = DropKind.Database
            Else
                Throw Err("unsupported DROP object, only DATABASE/TABLE/INDEX are supported!")
            End If

            If AcceptKeyword("IF") Then
                ExpectKeyword("EXISTS")
                stmt.IfExists = True
            End If

            stmt.Name = ExpectIdentifier()

            If stmt.Kind = DropKind.Index Then
                ExpectKeyword("ON")
                stmt.OnTable = ExpectIdentifier()
            End If

            Return stmt
        End Function

        Private Function ParseShow() As ShowStatement
            ExpectKeyword("SHOW")

            Dim stmt As New ShowStatement

            If AcceptKeyword("DATABASES") OrElse AcceptKeyword("SCHEMAS") Then
                stmt.Kind = ShowKind.Databases
                Return stmt
            End If

            If AcceptKeyword("CREATE") Then
                Throw Err("SHOW CREATE is not supported yet, use DESCRIBE instead!")
            End If

            If AcceptKeyword("INDEX") OrElse AcceptKeyword("INDEXES") OrElse AcceptKeyword("KEYS") Then
                stmt.Kind = ShowKind.Indexes
                AcceptKeyword("FROM")
                AcceptKeyword("IN")
                stmt.Target = ExpectIdentifier()
                Return stmt
            End If

            If AcceptKeyword("COLUMNS") OrElse AcceptKeyword("FIELDS") Then
                stmt.Kind = ShowKind.Columns
                AcceptKeyword("FROM")
                AcceptKeyword("IN")
                stmt.Target = ExpectIdentifier()
                Return stmt
            End If

            ExpectKeyword("TABLES")
            stmt.Kind = ShowKind.Tables

            If AcceptKeyword("FROM") OrElse AcceptKeyword("IN") Then
                stmt.Target = ExpectIdentifier()
            End If

            Return stmt
        End Function

    End Class
End Namespace
