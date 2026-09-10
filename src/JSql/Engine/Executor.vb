Imports JSql.Sql
Imports JSql.Storage

Namespace Engine

    ''' <summary>
    ''' executes parsed sql statements against the file based storage layer
    ''' </summary>
    Public Class SqlExecutor

        ''' <summary>one physical table loaded into memory, kept visible under its sql alias</summary>
        Private Class TableRef
            Public Property AliasName As String
            Public Property Schema As TableSchema
            Public Property Rows As List(Of Dictionary(Of String, Object))
        End Class

        Private ReadOnly engine As SqlEngine

        Sub New(engine As SqlEngine)
            Me.engine = engine
        End Sub

        Private Function CurrentDb() As String
            Dim db As String = engine.Catalog.CurrentDatabase

            If db Is Nothing Then
                Throw New SqlError("no database selected, run USE <database> first")
            End If

            Return db
        End Function

        ' ==================== SELECT ====================

        Public Function ExecuteSelect(stmt As SelectStatement) As ResultSet
            Dim db As String = Nothing
            Dim refs As New List(Of TableRef)
            Dim scopes As List(Of RowScope)

            If stmt.FromTable Is Nothing Then
                scopes = New List(Of RowScope)

                Dim empty As New RowScope
                empty.Add("@ROW", New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase))
                scopes.Add(empty)
            Else
                db = CurrentDb()

                Dim base As StoredTable = engine.Catalog.LoadTable(db, stmt.FromTable)

                refs.Add(New TableRef With {
                    .AliasName = If(stmt.FromAlias, stmt.FromTable),
                    .Schema = base.Schema,
                    .Rows = NarrowByIndex(db, stmt.FromTable, base, stmt.Where, stmt.Joins.Count > 0)
                })

                For Each jc In stmt.Joins
                    Dim joined As StoredTable = engine.Catalog.LoadTable(db, jc.Table)

                    refs.Add(New TableRef With {
                        .AliasName = If(jc.Alias, jc.Table),
                        .Schema = joined.Schema,
                        .Rows = joined.Rows
                    })
                Next

                scopes = BuildScopes(refs, stmt.Joins, stmt.Where)
            End If

            Dim projections As List(Of SelectItem) = BuildProjections(stmt, refs)
            Dim hasAggregate As Boolean = stmt.Having IsNot Nothing AndAlso ExpressionEvaluator.ContainsAggregate(stmt.Having)

            For Each item In projections
                If ExpressionEvaluator.ContainsAggregate(item.Expression) Then
                    hasAggregate = True
                End If
            Next

            Dim allColumns As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For Each ref In refs
                For Each col In ref.Schema.Columns
                    allColumns.Add(col.Name)
                Next
            Next

            If stmt.GroupBy.Count > 0 OrElse (hasAggregate AndAlso stmt.FromTable IsNot Nothing) Then
                Return ExecuteGrouped(stmt, scopes, projections, allColumns)
            End If

            Return ExecutePlain(stmt, scopes, projections)
        End Function

        ''' <summary>
        ''' use the column search index to narrow the candidate rows before the
        ''' full WHERE expression is evaluated; returns the full row list when no
        ''' usable index exists.
        ''' </summary>
        Private Function NarrowByIndex(db As String, table As String, stored As StoredTable,
                                       where As Expression, hasJoin As Boolean) As List(Of Dictionary(Of String, Object))
            If hasJoin OrElse where Is Nothing Then
                Return stored.Rows
            End If

            Dim candidate As Integer() = engine.Indexes.Probe(db, table, stored, where)

            If candidate Is Nothing Then
                Return stored.Rows
            End If

            Dim subset As New List(Of Dictionary(Of String, Object))

            For Each i In candidate
                If i >= 0 AndAlso i < stored.Rows.Count Then
                    subset.Add(stored.Rows(i))
                End If
            Next

            Return subset
        End Function

        Private Function BuildScopes(refs As List(Of TableRef), joins As List(Of JoinClause),
                                     where As Expression) As List(Of RowScope)
            Dim scopes As New List(Of RowScope)

            For Each row In refs(0).Rows
                Dim scope As New RowScope
                scope.Add(refs(0).AliasName, row)
                scopes.Add(scope)
            Next

            For j As Integer = 0 To joins.Count - 1
                Dim rhs As TableRef = refs(j + 1)
                Dim jc As JoinClause = joins(j)
                Dim merged As New List(Of RowScope)

                For Each lhs In scopes
                    Dim matched As Boolean = False

                    For Each row In rhs.Rows
                        Dim probe As RowScope = lhs.Clone()
                        probe.Add(rhs.AliasName, row)

                        If jc.On Is Nothing OrElse ExpressionEvaluator.IsTrue(New ExpressionEvaluator(probe).Eval(jc.On)) Then
                            Dim keep As RowScope = lhs.Clone()
                            keep.Add(rhs.AliasName, row)
                            merged.Add(keep)
                            matched = True
                        End If
                    Next

                    If Not matched AndAlso jc.JoinType = "lhs" Then
                        Dim keep As RowScope = lhs.Clone()
                        keep.Add(rhs.AliasName, Nothing)
                        merged.Add(keep)
                    End If
                Next

                scopes = merged
            Next

            If where IsNot Nothing Then
                Dim kept As New List(Of RowScope)

                For Each scope In scopes
                    If ExpressionEvaluator.IsTrue(New ExpressionEvaluator(scope).Eval(where)) Then
                        kept.Add(scope)
                    End If
                Next

                scopes = kept
            End If

            Return scopes
        End Function

        Private Function BuildProjections(stmt As SelectStatement, refs As List(Of TableRef)) As List(Of SelectItem)
            Dim columnMap As New List(Of KeyValuePair(Of String, List(Of String)))

            For Each ref In refs
                Dim cols As New List(Of String)

                For Each col In ref.Schema.Columns
                    cols.Add(col.Name)
                Next

                columnMap.Add(New KeyValuePair(Of String, List(Of String))(ref.AliasName, cols))
            Next

            Dim list As New List(Of SelectItem)

            For Each item In stmt.SelectItems
                Dim star As StarExpression = TryCast(item.Expression, StarExpression)

                If star Is Nothing Then
                    list.Add(item)
                    Continue For
                End If

                If star.Qualifier IsNot Nothing Then
                    Dim cols As List(Of String) = Nothing

                    For Each kv In columnMap
                        If String.Equals(kv.Key, star.Qualifier, StringComparison.OrdinalIgnoreCase) Then
                            cols = kv.Value
                            Exit For
                        End If
                    Next

                    If cols Is Nothing Then
                        Throw New SqlError("unknown table alias in the select list: " & star.Qualifier)
                    End If

                    For Each c In cols
                        list.Add(New SelectItem With {.Expression = New IdentifierExpression(star.Qualifier, c), .Alias = c})
                    Next
                Else
                    If columnMap.Count = 0 Then
                        Throw New SqlError("no table available for the SELECT * expression")
                    End If

                    For Each kv In columnMap
                        For Each c In kv.Value
                            list.Add(New SelectItem With {.Expression = New IdentifierExpression(kv.Key, c), .Alias = c})
                        Next
                    Next
                End If
            Next

            If list.Count = 0 Then
                Throw New SqlError("empty select list!")
            End If

            Return list
        End Function

        Private Shared Function ColumnName(item As SelectItem, index As Integer) As String
            If item.Alias IsNot Nothing Then
                Return item.Alias
            End If

            Dim id As IdentifierExpression = TryCast(item.Expression, IdentifierExpression)

            If id IsNot Nothing Then
                Return id.Name
            End If

            Return "expr" & (index + 1)
        End Function

        Private Function ExecutePlain(stmt As SelectStatement, scopes As List(Of RowScope),
                                      projections As List(Of SelectItem)) As ResultSet
            Dim columns As New List(Of String)

            For i As Integer = 0 To projections.Count - 1
                columns.Add(ColumnName(projections(i), i))
            Next

            Dim buffered As New List(Of KeyValuePair(Of Object(), Object()))

            For Each scope In scopes
                Dim ev As New ExpressionEvaluator(scope)
                Dim values(projections.Count - 1) As Object

                For i As Integer = 0 To projections.Count - 1
                    values(i) = ev.Eval(projections(i).Expression)
                Next

                Dim keys As Object() = Nothing

                If stmt.OrderBy.Count > 0 Then
                    keys = New Object(stmt.OrderBy.Count - 1) {}

                    For k As Integer = 0 To stmt.OrderBy.Count - 1
                        Dim key As Expression = stmt.OrderBy(k).Expression
                        Dim col As IdentifierExpression = TryCast(key, IdentifierExpression)

                        If col IsNot Nothing AndAlso columns.Contains(col.Name) Then
                            ' mysql resolves ORDER BY against the output column list first
                            keys(k) = values(columns.IndexOf(col.Name))
                        ElseIf TypeOf key Is LiteralExpression Then
                            Dim lit = DirectCast(key, LiteralExpression)

                            If lit.IsNull OrElse lit.Value Is Nothing Then
                                keys(k) = Nothing
                            ElseIf SqlTypes.IsNumericValue(lit.Value) Then
                                Dim ordinal As Integer = CInt(Math.Round(CDbl(lit.Value))) - 1

                                If ordinal >= 0 AndAlso ordinal < values.Length Then
                                    keys(k) = values(ordinal)
                                Else
                                    keys(k) = Nothing
                                End If
                            Else
                                keys(k) = lit.Value
                            End If
                        Else
                            keys(k) = ev.Eval(key)
                        End If
                    Next
                End If

                buffered.Add(New KeyValuePair(Of Object(), Object())(values, keys))
            Next

            If stmt.OrderBy.Count > 0 Then
                buffered.Sort(Function(a, b) CompareRows(a, b, stmt.OrderBy))
            End If

            Dim rows As List(Of Object()) = ApplyDistinctAndLimit(stmt, buffered)

            Return ResultSet.FromQuery(columns, rows)
        End Function

        Private Function CompareRows(a As KeyValuePair(Of Object(), Object()),
                                     b As KeyValuePair(Of Object(), Object()),
                                     orderBy As List(Of OrderItem)) As Integer
            For i As Integer = 0 To orderBy.Count - 1
                Dim item As OrderItem = orderBy(i)
                Dim av As Object = a.Value(i)
                Dim bv As Object = b.Value(i)

                If av Is Nothing AndAlso bv Is Nothing Then
                    Continue For
                End If

                If av Is Nothing Then
                    Return If(item.Descending, 1, -1)
                End If

                If bv Is Nothing Then
                    Return If(item.Descending, -1, 1)
                End If

                Dim cmp As Integer = SqlTypes.CompareValues(av, bv)

                If cmp <> 0 Then
                    Return If(item.Descending, -cmp, cmp)
                End If
            Next

            Return 0
        End Function

        Private Function ApplyDistinctAndLimit(stmt As SelectStatement,
                                               buffered As List(Of KeyValuePair(Of Object(), Object()))) As List(Of Object())
            Dim rows As New List(Of Object())

            If stmt.Distinct Then
                Dim seen As New HashSet(Of String)(StringComparer.Ordinal)

                For Each pair In buffered
                    Dim parts As New List(Of String)

                    For Each v In pair.Key
                        parts.Add(If(v Is Nothing, Chr(1) & "NULL", Convert.ToString(v)))
                    Next

                    If seen.Add(String.Join(Chr(1), parts.ToArray())) Then
                        rows.Add(pair.Key)
                    End If
                Next
            Else
                For Each pair In buffered
                    rows.Add(pair.Key)
                Next
            End If

            If stmt.Offset > 0 Then
                rows.RemoveRange(0, Math.Min(stmt.Offset, rows.Count))
            End If

            If stmt.Limit >= 0 AndAlso rows.Count > stmt.Limit Then
                rows.RemoveRange(stmt.Limit, rows.Count - stmt.Limit)
            End If

            Return rows
        End Function

        ''' <summary>
        ''' mysql resolves a bare identifier in HAVING against the output column aliases
        ''' first, so the alias is rewritten into its projection expression here.
        ''' </summary>
        Private Shared Function RewriteAliases(expr As Expression, projections As List(Of SelectItem),
                                               allColumns As HashSet(Of String)) As Expression
            If expr Is Nothing Then
                Return Nothing
            End If

            If TypeOf expr Is IdentifierExpression Then
                Dim id = DirectCast(expr, IdentifierExpression)

                If id.Qualifier IsNot Nothing OrElse allColumns.Contains(id.Name) Then
                    Return expr
                End If

                For Each item In projections
                    If String.Equals(item.Alias, id.Name, StringComparison.OrdinalIgnoreCase) Then
                        Return item.Expression
                    End If
                Next

                Return expr
            End If

            If TypeOf expr Is BinaryExpression Then
                Dim b = DirectCast(expr, BinaryExpression)

                Return New BinaryExpression With {
                    .Op = b.Op,
                    .Left = RewriteAliases(b.Left, projections, allColumns),
                    .Right = RewriteAliases(b.Right, projections, allColumns)
                }
            End If

            If TypeOf expr Is UnaryExpression Then
                Dim u = DirectCast(expr, UnaryExpression)

                Return New UnaryExpression With {.Op = u.Op, .Operand = RewriteAliases(u.Operand, projections, allColumns)}
            End If

            If TypeOf expr Is IsNullExpression Then
                Dim n = DirectCast(expr, IsNullExpression)

                Return New IsNullExpression With {.Negated = n.Negated, .Operand = RewriteAliases(n.Operand, projections, allColumns)}
            End If

            Return expr
        End Function

        Private Function ExecuteGrouped(stmt As SelectStatement, scopes As List(Of RowScope),
                                        projections As List(Of SelectItem), allColumns As HashSet(Of String)) As ResultSet
        Dim having As Expression = RewriteAliases(stmt.Having, projections, allColumns)
            Dim groups As New List(Of List(Of RowScope))
            Dim groupKeys As New List(Of Object())

            If stmt.GroupBy.Count = 0 Then
                groups.Add(scopes)
                groupKeys.Add(New Object() {})
            Else
                For Each scope In scopes
                    Dim ev As New ExpressionEvaluator(scope)
                    Dim key(stmt.GroupBy.Count - 1) As Object

                    For i As Integer = 0 To stmt.GroupBy.Count - 1
                        key(i) = ev.Eval(stmt.GroupBy(i))
                    Next

                    Dim slot As Integer = FindGroup(groupKeys, key)

                    If slot < 0 Then
                        groupKeys.Add(key)
                        groups.Add(New List(Of RowScope) From {scope})
                    Else
                        groups(slot).Add(scope)
                    End If
                Next
            End If

            Dim columns As New List(Of String)

            For i As Integer = 0 To projections.Count - 1
                columns.Add(ColumnName(projections(i), i))
            Next

            Dim buffered As New List(Of KeyValuePair(Of Object(), Object()))

            For g As Integer = 0 To groups.Count - 1
                Dim rows As List(Of RowScope) = groups(g)
                Dim representative As RowScope = If(rows.Count > 0, rows(0), New RowScope())
                Dim ev As New ExpressionEvaluator(representative, rows)

                If having IsNot Nothing Then
                    If Not ExpressionEvaluator.IsTrue(ev.Eval(having)) Then
                        Continue For
                    End If
                End If

                Dim values(projections.Count - 1) As Object

                For i As Integer = 0 To projections.Count - 1
                    values(i) = ev.Eval(projections(i).Expression)
                Next

                Dim keys As Object() = Nothing

                If stmt.OrderBy.Count > 0 Then
                    keys = New Object(stmt.OrderBy.Count - 1) {}

                    For k As Integer = 0 To stmt.OrderBy.Count - 1
                        Dim key As Expression = stmt.OrderBy(k).Expression
                        Dim col As IdentifierExpression = TryCast(key, IdentifierExpression)

                        If col IsNot Nothing AndAlso columns.Contains(col.Name) Then
                            keys(k) = values(columns.IndexOf(col.Name))
                        Else
                            keys(k) = ev.Eval(key)
                        End If
                    Next
                End If

                buffered.Add(New KeyValuePair(Of Object(), Object())(values, keys))
            Next

            If stmt.OrderBy.Count > 0 Then
                buffered.Sort(Function(a, b) CompareRows(a, b, stmt.OrderBy))
            End If

            Return ResultSet.FromQuery(columns, ApplyDistinctAndLimit(stmt, buffered))
        End Function

        Private Function FindGroup(keys As List(Of Object()), key As Object()) As Integer
            For i As Integer = 0 To keys.Count - 1
                If SameKey(keys(i), key) Then
                    Return i
                End If
            Next

            Return -1
        End Function

        Private Function SameKey(a As Object(), b As Object()) As Boolean
            If a.Length <> b.Length Then
                Return False
            End If

            For i As Integer = 0 To a.Length - 1
                If a(i) Is Nothing AndAlso b(i) Is Nothing Then
                    Continue For
                End If

                If a(i) Is Nothing OrElse b(i) Is Nothing Then
                    Return False
                End If

                If SqlTypes.CompareValues(a(i), b(i)) <> 0 Then
                    Return False
                End If
            Next

            Return True
        End Function

        ' ==================== INSERT / UPDATE / DELETE ====================

        Public Function ExecuteInsert(stmt As InsertStatement) As ResultSet
            Dim db As String = CurrentDb()
            Dim stored As StoredTable = engine.Catalog.LoadTable(db, stmt.Table)
            Dim targets As List(Of ColumnDef) = New List(Of ColumnDef)()

            If stmt.Columns Is Nothing Then
                targets.AddRange(stored.Schema.Columns)
            Else
                For Each name In stmt.Columns
                    Dim col As ColumnDef = stored.Schema.FindColumn(name)

                    If col Is Nothing Then
                        Throw New SqlError("unknown column " & name & " in table " & stmt.Table)
                    End If

                    targets.Add(col)
                Next
            End If

            Dim ev As New ExpressionEvaluator(New RowScope())
            Dim added As Integer = 0

            For Each valueRow In stmt.ValueRows
                If valueRow.Count <> targets.Count Then
                    Throw New SqlError("column count does not match the value count at row " & (added + 1))
                End If

                Dim row As Dictionary(Of String, Object) = stored.NewRow()

                For i As Integer = 0 To targets.Count - 1
                    Dim raw As Object = ev.Eval(valueRow(i))
                    Dim col As ColumnDef = targets(i)
                    Dim value As Object = SqlTypes.CoerceValue(raw, col.TypeName)

                    If value Is Nothing Then
                        If col.NotNull Then
                            Throw New SqlError("column " & col.Name & " can not be null")
                        End If

                        value = col.DefaultValue
                    End If

                    row(col.Name) = value
                Next

                For Each col In stored.Schema.Columns
                    If Not row.ContainsKey(col.Name) Then
                        row(col.Name) = SqlTypes.CoerceValue(col.DefaultValue, col.TypeName)
                    End If
                Next

                stored.Rows.Add(row)
                added += 1
            Next

            engine.Catalog.SaveTable(db, stored)
            engine.Indexes.RebuildAfterWrite(db, stmt.Table, stored)

            Return ResultSet.FromMessage("Query OK, " & added & " row(s) affected.", added)
        End Function

        Public Function ExecuteUpdate(stmt As UpdateStatement) As ResultSet
            Dim db As String = CurrentDb()
            Dim stored As StoredTable = engine.Catalog.LoadTable(db, stmt.Table)
            Dim candidates As List(Of Dictionary(Of String, Object)) = NarrowByIndex(db, stmt.Table, stored, stmt.Where, False)
            Dim ev As New ExpressionEvaluator(New RowScope())
            Dim changed As Integer = 0

            For Each row In candidates
                Dim scope As New RowScope
                scope.Add(stmt.Table, row)

                If stmt.Where IsNot Nothing AndAlso Not ExpressionEvaluator.IsTrue(New ExpressionEvaluator(scope).Eval(stmt.Where)) Then
                    Continue For
                End If

                For Each a In stmt.Assignments
                    Dim col As ColumnDef = stored.Schema.FindColumn(a.Column)

                    If col Is Nothing Then
                        Throw New SqlError("unknown column " & a.Column & " in table " & stmt.Table)
                    End If

                    Dim value As Object = SqlTypes.CoerceValue(New ExpressionEvaluator(scope).Eval(a.Value), col.TypeName)

                    If value Is Nothing AndAlso col.NotNull Then
                        Throw New SqlError("column " & col.Name & " can not be null")
                    End If

                    If ExpressionEvaluator.ContainsAggregate(a.Value) Then
                        Throw New SqlError("aggregate functions are not allowed in the SET clause")
                    End If

                    row(col.Name) = value
                Next

                changed += 1
            Next

            If changed > 0 Then
                engine.Catalog.SaveTable(db, stored)
                engine.Indexes.RebuildAfterWrite(db, stmt.Table, stored)
            End If

            Return ResultSet.FromMessage("Query OK, " & changed & " row(s) affected.", changed)
        End Function

        Public Function ExecuteDelete(stmt As DeleteStatement) As ResultSet
            Dim db As String = CurrentDb()
            Dim stored As StoredTable = engine.Catalog.LoadTable(db, stmt.Table)
            Dim keep As List(Of Dictionary(Of String, Object)) = New List(Of Dictionary(Of String, Object))()
            Dim removed As Integer = 0

            For Each row In stored.Rows
                Dim scope As New RowScope
                scope.Add(stmt.Table, row)

                If stmt.Where IsNot Nothing AndAlso Not ExpressionEvaluator.IsTrue(New ExpressionEvaluator(scope).Eval(stmt.Where)) Then
                    keep.Add(row)
                Else
                    removed += 1
                End If
            Next

            If removed > 0 Then
                stored.Rows = keep
                engine.Catalog.SaveTable(db, stored)
                engine.Indexes.RebuildAfterWrite(db, stmt.Table, stored)
            End If

            Return ResultSet.FromMessage("Query OK, " & removed & " row(s) affected.", removed)
        End Function

        ' ==================== DDL / meta ====================

        Public Function ExecuteCreate(stmt As CreateStatement) As ResultSet
            If stmt.Kind = CreateKind.Database Then
                If engine.Catalog.DatabaseExists(stmt.Name) Then
                    If stmt.IfNotExists Then
                        Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                    End If

                    Throw New SqlError("database " & stmt.Name & " already exists")
                End If

                engine.Catalog.CreateDatabase(stmt.Name)
                Return ResultSet.FromMessage("Query OK, 1 database created.")
            End If

            Dim db As String = CurrentDb()

            If stmt.Kind = CreateKind.Table Then
                If engine.Catalog.TableExists(db, stmt.Name) Then
                    If stmt.IfNotExists Then
                        Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                    End If

                    Throw New SqlError("table " & stmt.Name & " already exists")
                End If

                Dim table As New StoredTable With {
                    .Schema = New TableSchema With {
                        .TableName = stmt.Name,
                        .Comment = stmt.TableComment
                    }
                }

                Dim primary As Boolean = False

                For Each col In stmt.Columns
                    If table.Schema.HasColumn(col.Name) Then
                        Throw New SqlError("duplicate column name: " & col.Name)
                    End If

                    If col.PrimaryKey Then
                        If primary Then
                            Throw New SqlError("only one primary key column is supported")
                        End If

                        primary = True
                    End If

                    table.Schema.Columns.Add(col)
                Next

                ' a table level PRIMARY KEY(col) also marks the column itself
                For Each key In stmt.Keys
                    table.Schema.Keys.Add(key)

                    If key.Primary Then
                        For Each keyColumn In key.Columns
                            Dim target As ColumnDef = table.Schema.FindColumn(keyColumn)

                            If target Is Nothing Then
                                Throw New SqlError("primary key references an unknown column: " & keyColumn)
                            End If

                            If primary AndAlso Not target.PrimaryKey Then
                                Throw New SqlError("only one primary key column is supported")
                            End If

                            target.PrimaryKey = True
                            target.NotNull = True
                            primary = True
                        Next
                    End If
                Next

                engine.Catalog.SaveTable(db, table)
                Return ResultSet.FromMessage("Query OK, table " & stmt.Name & " created.")
            End If

            Dim stored As StoredTable = engine.Catalog.LoadTable(db, stmt.OnTable)
            Dim kind As String = If(stmt.IndexKind, engine.Indexes.DefaultKindFor(stmt.OnColumn, stored.Schema))

            If engine.Indexes.Exists(db, stmt.OnTable, stmt.Name) Then
                If stmt.IfNotExists Then
                    Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                End If

                Throw New SqlError("index " & stmt.Name & " already exists")
            End If

            engine.Indexes.CreateIndex(db, stmt.OnTable, stored, stmt.Name, stmt.OnColumn, kind)
            Return ResultSet.FromMessage("Query OK, index " & stmt.Name & " created.")
        End Function

        Public Function ExecuteDrop(stmt As DropStatement) As ResultSet
            If stmt.Kind = DropKind.Database Then
                If Not engine.Catalog.DatabaseExists(stmt.Name) Then
                    If stmt.IfExists Then
                        Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                    End If

                    Throw New SqlError("database " & stmt.Name & " does not exist")
                End If

                engine.Catalog.DropDatabase(stmt.Name)

                If String.Equals(engine.Catalog.CurrentDatabase, stmt.Name, StringComparison.OrdinalIgnoreCase) Then
                    engine.Catalog.CurrentDatabase = Nothing
                End If

                Return ResultSet.FromMessage("Query OK, database " & stmt.Name & " dropped.")
            End If

            Dim db As String = CurrentDb()

            If stmt.Kind = DropKind.Table Then
                If Not engine.Catalog.TableExists(db, stmt.Name) Then
                    If stmt.IfExists Then
                        Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                    End If

                    Throw New SqlError("table " & stmt.Name & " does not exist")
                End If

                engine.Indexes.DropAllForTable(db, stmt.Name)
                engine.Catalog.DeleteTable(db, stmt.Name)
                Return ResultSet.FromMessage("Query OK, table " & stmt.Name & " dropped.")
            End If

            If Not engine.Catalog.TableExists(db, stmt.OnTable) Then
                Throw New SqlError("table " & stmt.OnTable & " does not exist")
            End If

            If Not engine.Indexes.DropIndex(db, stmt.OnTable, stmt.Name) Then
                If stmt.IfExists Then
                    Return ResultSet.FromMessage("Query OK, 0 row(s) affected.")
                End If

                Throw New SqlError("index " & stmt.Name & " does not exist")
            End If

            Return ResultSet.FromMessage("Query OK, index " & stmt.Name & " dropped.")
        End Function

        Public Function ExecuteUse(stmt As UseStatement) As ResultSet
            If Not engine.Catalog.DatabaseExists(stmt.Database) Then
                Throw New SqlError("unknown database: " & stmt.Database)
            End If

            engine.Catalog.CurrentDatabase = stmt.Database
            Return ResultSet.FromMessage("Database changed to " & stmt.Database)
        End Function

        Public Function ExecuteShow(stmt As ShowStatement) As ResultSet
            Select Case stmt.Kind
                Case ShowKind.Databases
                    Dim names As List(Of String) = engine.Catalog.GetDatabases()
                    Dim rows As New List(Of Object())

                    For Each name In names
                        rows.Add(New Object() {name})
                    Next

                    Return ResultSet.FromQuery(New String() {"Database"}, rows)

                Case ShowKind.Tables
                    Dim db As String = If(stmt.Target, CurrentDb())
                    Dim names As List(Of String) = engine.Catalog.GetTables(db)
                    Dim rows As New List(Of Object())

                    For Each name In names
                        rows.Add(New Object() {name})
                    Next

                    Return ResultSet.FromQuery(New String() {"Tables_in_" & db}, rows)

                Case ShowKind.Indexes
                    Dim db As String = CurrentDb()
                    Dim rows As New List(Of Object())

                    For Each index In engine.Indexes.GetIndexSet(db, stmt.Target).Indexes
                        rows.Add(New Object() {index.Name, index.Column, index.Kind.ToString(), index.ValueType})
                    Next

                    Return ResultSet.FromQuery(New String() {"Index", "Column", "Kind", "Type"}, rows)

                Case Else
                    Dim dbx As String = CurrentDb()
                    Dim stored As StoredTable = engine.Catalog.LoadTable(dbx, stmt.Target)
                    Dim rowsx As New List(Of Object())

                    For Each col In stored.Schema.Columns
                        rowsx.Add(New Object() {col.Name, col.TypeName, If(col.NotNull, "NO", "YES"),
                                                If(col.PrimaryKey, "PRI", ""), If(col.DefaultValue, Nothing),
                                                If(col.Comment, Nothing)})
                    Next

                    Return ResultSet.FromQuery(New String() {"Field", "Type", "Null", "Key", "Default", "Comment"}, rowsx)
            End Select
        End Function
    End Class
End Namespace
