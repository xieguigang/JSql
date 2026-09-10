Imports System.Collections.Generic
Imports JSql.Sql
Imports JSql.Storage
Imports LINQ

Namespace Indexing

    Public Enum IndexKind
        ''' <summary>term hash index, used by the string equality condition</summary>
        Hash
        ''' <summary>range index, used by the numeric and date comparison</summary>
        Range
        ''' <summary>full text index, maintained but not yet used by the planner</summary>
        FullText
    End Enum

    Public Class ColumnIndexInfo

        Public Property Name As String
        Public Property Column As String
        Public Property Kind As IndexKind
        ''' <summary>the clr value type of the range index: Integer/Double/Date</summary>
        Public Property ValueType As String

        Public Function KindText() As String
            Return Kind.ToString().ToUpper()
        End Function
    End Class

    Public Class TableIndexSet

        Public Property Database As String
        Public Property Table As String
        Public Property Indexes As New List(Of ColumnIndexInfo)
        ''' <summary>
        ''' the archived index data of each column, used to restore the search index
        ''' from disk instead of re-indexing the whole table on every startup.
        ''' </summary>
        Public Property Archives As New Dictionary(Of String, IndexArchive)(StringComparer.OrdinalIgnoreCase)

        Public Function FindByName(name As String) As ColumnIndexInfo
            For Each index In Indexes
                If String.Equals(index.Name, name, StringComparison.OrdinalIgnoreCase) Then
                    Return index
                End If
            Next

            Return Nothing
        End Function

        Public Function FindByColumn(column As String, kind As IndexKind) As ColumnIndexInfo
            For Each index In Indexes
                If String.Equals(index.Column, column, StringComparison.OrdinalIgnoreCase) AndAlso index.Kind = kind Then
                    Return index
                End If
            Next

            Return Nothing
        End Function
    End Class

    ''' <summary>
    ''' index facade of the sql engine: maintains the search index of every table,
    ''' probes usable indexes for a WHERE clause and keeps the index files in sync
    ''' with the table data files.
    ''' </summary>
    Public Class IndexManager

        Private ReadOnly catalog As DatabaseCatalog
        Private ReadOnly indexSets As New Dictionary(Of String, TableIndexSet)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly builtObjects As New Dictionary(Of String, JsonMemoryIndex)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly builtRowCount As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

        Sub New(catalog As DatabaseCatalog)
            Me.catalog = catalog
        End Sub

        Private Shared Function TableKey(db As String, table As String) As String
            Return db & "/" & table
        End Function

        Public Function DefaultKindFor(column As String, schema As TableSchema) As String
            Dim col As ColumnDef = schema.FindColumn(column)

            If col Is Nothing Then
                Throw New SqlError("unknown column named: " & column)
            End If

            Select Case col.TypeName
                Case "INT", "DOUBLE", "DATE", "DATETIME"
                    Return "RANGE"
                Case Else
                    Return "HASH"
            End Select
        End Function

        Public Function GetIndexSet(db As String, table As String) As TableIndexSet
            Dim key As String = TableKey(db, table)

            If indexSets.ContainsKey(key) Then
                Return indexSets(key)
            End If

            Dim sets As New TableIndexSet With {.Database = db, .Table = table}
            Dim archives As List(Of IndexArchive) = IndexPersistence.LoadTableArchives(catalog.DatabaseDir(db), table)

            For Each archive In archives
                sets.Indexes.Add(New ColumnIndexInfo With {
                    .Name = archive.name,
                    .Column = archive.column,
                    .Kind = ParseKind(archive.kind),
                    .ValueType = archive.valueType
                })

                If archive.column IsNot Nothing Then
                    sets.Archives(archive.column) = archive
                End If
            Next

            indexSets(key) = sets
            Return sets
        End Function

        Private Shared Function ParseKind(kind As String) As IndexKind
            Select Case kind.ToUpper()
                Case "RANGE", "BTREE" : Return IndexKind.Range
                Case "FULLTEXT", "FTS" : Return IndexKind.FullText
                Case Else : Return IndexKind.Hash
            End Select
        End Function

        Public Function Exists(db As String, table As String, name As String) As Boolean
            Return GetIndexSet(db, table).FindByName(name) IsNot Nothing
        End Function


        ' ==================== index building ====================

        Public Function EnsureBuilt(db As String, table As String, stored As StoredTable) As JsonMemoryIndex
            Dim key As String = TableKey(db, table)

            If builtObjects.ContainsKey(key) AndAlso builtRowCount(key) = stored.Rows.Count Then
                Return builtObjects(key)
            End If

            Dim sets As TableIndexSet = GetIndexSet(db, table)
            Dim memory As New JsonMemoryIndex(stored.Rows)

            For Each columnIndex In sets.Indexes
                Select Case columnIndex.Kind
                    Case IndexKind.Hash
                        If Not RestoreHashIndex(memory, sets, columnIndex, stored.Rows.Count) Then
                            Call memory.BuildHash(columnIndex.Column)
                        End If
                    Case IndexKind.Range
                        Call memory.BuildRange(columnIndex.Column, RangeType(columnIndex.ValueType))
                    Case IndexKind.FullText
                        Call memory.BuildFullText(columnIndex.Column)
                End Select
            Next

            builtObjects(key) = memory
            builtRowCount(key) = stored.Rows.Count
            Return memory
        End Function

        Private Shared Function RangeType(valueType As String) As Type
            Select Case valueType
                Case "Integer", "Int32" : Return GetType(Integer)
                Case "Double" : Return GetType(Double)
                Case "Date", "DateTime" : Return GetType(Date)
                Case Else : Throw New SqlError("unknown range index value type: " & valueType)
            End Select
        End Function

        ''' <summary>
        ''' rebuild a term hash index from its archived maps instead of re-indexing the
        ''' whole table. returns false when the archive is missing or out of date.
        ''' </summary>
        Private Shared Function RestoreHashIndex(memory As JsonMemoryIndex, sets As TableIndexSet,
                                                columnIndex As ColumnIndexInfo, rowCount As Integer) As Boolean
            Dim archive As IndexArchive = Nothing

            If Not sets.Archives.TryGetValue(columnIndex.Column, archive) Then
                Return False
            End If

            If archive Is Nothing OrElse archive.hashMaps Is Nothing OrElse archive.documentMaps Is Nothing Then
                Return False
            End If

            ' the archived maps are only valid for the exact row set they were built from,
            ' an UPDATE keeps the row count unchanged so the column content snapshot is
            ' compared here as well.
            If archive.rowCount <> rowCount OrElse archive.documents Is Nothing Then
                Return False
            End If

            Dim current As String() = memory.ColumnText(columnIndex.Column)

            If current.Length <> archive.documents.Count Then
                Return False
            End If

            For i As Integer = 0 To current.Length - 1
                If Not String.Equals(current(i), archive.documents(i), StringComparison.Ordinal) Then
                    Return False
                End If
            Next

            Dim documentMaps As New Dictionary(Of Integer, Integer)()

            For Each kv In archive.documentMaps
                documentMaps(kv.Key) = kv.Value
            Next

            Dim hashMaps As New Dictionary(Of String, Integer())()

            For Each kv In archive.hashMaps
                hashMaps(kv.Key) = kv.Value
            Next

            memory.RestoreTermIndex(columnIndex.Column, New TermHashIndex(New InMemoryDocuments(), documentMaps, hashMaps))
            Return True
        End Function

        ''' <summary>the persisted name of the clr type of a range index</summary>
        Private Shared Function RangeValueTypeName(valueType As Type) As String
            If valueType Is GetType(Integer) Then
                Return "Integer"
            ElseIf valueType Is GetType(Date) Then
                Return "Date"
            Else
                Return "Double"
            End If
        End Function

        Private Shared Function RangeTypeOf(schema As TableSchema, column As String) As Type
            Dim col As ColumnDef = schema.FindColumn(column)

            If col Is Nothing Then
                Throw New SqlError("unknown column named: " & column)
            End If

            Select Case col.TypeName
                Case "INT" : Return GetType(Integer)
                Case "DATE", "DATETIME" : Return GetType(Date)
                Case Else : Return GetType(Double)
            End Select
        End Function

        Public Sub Invalidate(db As String, table As String)
            Dim key As String = TableKey(db, table)

            builtObjects.Remove(key)
            builtRowCount.Remove(key)
        End Sub

        ' ==================== create / drop / rebuild ====================

        Public Function CreateIndex(db As String, table As String, stored As StoredTable,
                                    name As String, column As String, kindText As String) As ColumnIndexInfo
            If stored.Schema.FindColumn(column) Is Nothing Then
                Throw New SqlError("unknown column named: " & column)
            End If

            Dim sets As TableIndexSet = GetIndexSet(db, table)
            Dim kind As IndexKind = ParseKind(kindText)
            Dim columnIndex As New ColumnIndexInfo With {
                .Name = name,
                .Column = column,
                .Kind = kind,
                .ValueType = If(kind = IndexKind.Range, RangeValueTypeName(RangeTypeOf(stored.Schema, column)), "String")
            }

            sets.Indexes.Add(columnIndex)
            Invalidate(db, table)

            ' build and persist the index right away, roll the catalog entry back
            ' when anything goes wrong so that no broken index is left behind
            Try
                Persist(db, table, columnIndex, EnsureBuilt(db, table, stored), stored.Rows.Count, catalog.DatabaseDir(db))
            Catch ex As Exception
                sets.Indexes.Remove(columnIndex)
                Invalidate(db, table)
                Throw
            End Try

            Return columnIndex
        End Function

        Private Function Persist(db As String, table As String, columnIndex As ColumnIndexInfo,
                                 memory As JsonMemoryIndex, rowCount As Integer, dbDir As String) As String
            Dim archive As New IndexArchive With {
                .name = columnIndex.Name,
                .table = table,
                .column = columnIndex.Column,
                .kind = columnIndex.KindText(),
                .valueType = columnIndex.ValueType,
                .rowCount = rowCount,
                .documents = memory.ColumnText(columnIndex.Column).ToList()
            }

            If columnIndex.Kind = IndexKind.Hash Then
                archive.hashMaps = memory.TermIndex(columnIndex.Column).GetHashIndex()
                archive.documentMaps = memory.TermIndex(columnIndex.Column).GetDocumentMaps()
            End If

            ' refresh the in-memory archive cache so that a later restore never
            ' brings back an out of date index
            GetIndexSet(db, table).Archives(columnIndex.Column) = archive

            Return IndexPersistence.Save(archive, dbDir)
        End Function

        Public Function DropIndex(db As String, table As String, name As String) As Boolean
            Dim sets As TableIndexSet = GetIndexSet(db, table)
            Dim columnIndex As ColumnIndexInfo = sets.FindByName(name)

            If columnIndex Is Nothing Then
                Return False
            End If

            sets.Indexes.Remove(columnIndex)
            Invalidate(db, table)
            Return IndexPersistence.DropOne(catalog.DatabaseDir(db), table, name)
        End Function

        Public Sub DropAllForTable(db As String, table As String)
            indexSets.Remove(TableKey(db, table))
            Invalidate(db, table)
            IndexPersistence.DropTable(catalog.DatabaseDir(db), table)
        End Sub

        ''' <summary>
        ''' rebuild every index of one table after its data rows have been changed.
        ''' </summary>
        Public Sub RebuildAfterWrite(db As String, table As String, stored As StoredTable)
            Dim sets As TableIndexSet = GetIndexSet(db, table)

            If sets.Indexes.Count = 0 Then
                Invalidate(db, table)
                Return
            End If

            Invalidate(db, table)

            Dim memory As JsonMemoryIndex = EnsureBuilt(db, table, stored)
            Dim dbDir As String = catalog.DatabaseDir(db)

            For Each columnIndex In sets.Indexes
                Call Persist(db, table, columnIndex, memory, stored.Rows.Count, dbDir)
            Next
        End Sub
        ' ==================== index probing ====================

        ''' <summary>
        ''' translate the top level AND conditions of a WHERE clause into the LINQ
        ''' search index queries. returns the candidate row offsets of the given table,
        ''' or nothing when no usable index exists(first calldb the caller falls back to
        ''' a full table scan).
        ''' </summary>
        Public Function Probe(db As String, table As String, stored As StoredTable, where As Expression) As Integer()
            If where Is Nothing Then
                Return Nothing
            End If

            Dim sets As TableIndexSet = GetIndexSet(db, table)

            If sets.Indexes.Count = 0 Then
                Return Nothing
            End If

            Dim queries As New List(Of Query)

            For Each conjunct In SplitAnd(where)
                Dim q As Query = Nothing

                Try
                    q = TryMapQuery(conjunct, sets, stored.Schema)
                Catch ex As Exception
                    q = Nothing
                End Try

                If q IsNot Nothing Then
                    queries.Add(q)
                End If
            Next

            If queries.Count = 0 Then
                Return Nothing
            End If

            Try
                Dim hits As Integer() = EnsureBuilt(db, table, stored).SelectRowOffsets(queries)

                If Environment.GetEnvironmentVariable("JSQL_DEBUG_INDEX") = "1" Then
                    Console.Error.WriteLine("[index] queries=" & queries.Count & " rows=" & stored.Rows.Count &
                                            " hits=" & If(hits Is Nothing, "nothing", hits.Length.ToString()) &
                                            " -> " & String.Join(",", hits.Select(Function(i) i.ToString()).ToArray()))
                End If

                If hits Is Nothing Then
                    Return New Integer() {}
                End If

                Return hits
            Catch ex As Exception
                ' never let a broken index break the whole query: fall back to a full scan
                Console.Error.WriteLine("[index] index probe failed: " & ex.Message)
                Invalidate(db, table)
                Return Nothing
            End Try
        End Function

        Private Shared Function SplitAnd(expr As Expression) As List(Of Expression)
            Dim parts As New List(Of Expression)
            CollectAnd(expr, parts)
            Return parts
        End Function

        Private Shared Sub CollectAnd(expr As Expression, parts As List(Of Expression))
            Dim b As BinaryExpression = TryCast(expr, BinaryExpression)

            If b IsNot Nothing AndAlso b.Op = BinaryOp.And Then
                CollectAnd(b.Left, parts)
                CollectAnd(b.Right, parts)
            Else
                parts.Add(expr)
            End If
        End Sub

        ''' <summary>
        ''' map one sql comparison onto the search index query, nothing when the condition
        ''' can not be served by any available column index.
        ''' </summary>
        Private Function TryMapQuery(expr As Expression, sets As TableIndexSet, schema As TableSchema) As Query
            If TypeOf expr Is BetweenExpression Then
                Dim bt = DirectCast(expr, BetweenExpression)
                Dim column As String = ColumnOf(bt.Operand)

                If column Is Nothing OrElse Not IsConst(bt.Low) OrElse Not IsConst(bt.High) Then
                    Return Nothing
                End If

                Dim columnIndex As ColumnIndexInfo = sets.FindByColumn(column, IndexKind.Range)

                If columnIndex Is Nothing Then
                    Return Nothing
                End If

                Return New Query With {
                    .search = Query.Type.ValueRange,
                    .field = column,
                    .value = RangePair(columnIndex, ConstValue(bt.Low), ConstValue(bt.High))
                }
            End If

            Dim b As BinaryExpression = TryCast(expr, BinaryExpression)

            If b Is Nothing Then
                Return Nothing
            End If

            Dim op As BinaryOp = b.Op
            Dim left As Expression = b.Left
            Dim right As Expression = b.Right

            If op <> BinaryOp.Eq AndAlso op <> BinaryOp.Gt AndAlso op <> BinaryOp.Ge AndAlso
               op <> BinaryOp.Lt AndAlso op <> BinaryOp.Le Then
                Return Nothing
            End If

            ' normalize literal = column into column = literal
            If IsConst(left) AndAlso Not IsConst(right) Then
                Dim swap As Expression = left
                left = right
                right = swap
                op = Flip(op)
            End If

            Dim field As String = ColumnOf(left)

            If field Is Nothing OrElse Not IsConst(right) Then
                Return Nothing
            End If

            Dim value As Object = ConstValue(right)

            If op = BinaryOp.Eq Then
                Dim hashInfo As ColumnIndexInfo = sets.FindByColumn(field, IndexKind.Hash)

                If hashInfo IsNot Nothing Then
                    Return New Query With {
                        .search = Query.Type.HashTerm,
                        .field = field,
                        .value = Convert.ToString(value)
                    }
                End If

                Dim rangeInfo As ColumnIndexInfo = sets.FindByColumn(field, IndexKind.Range)

                If rangeInfo IsNot Nothing Then
                    Dim scalar As Object = RangeScalar(rangeInfo, value)

                    If scalar IsNot Nothing Then
                        Return New Query With {
                            .search = Query.Type.ValueMatch,
                            .field = field,
                            .value = scalar
                        }
                    End If
                End If

                Return Nothing
            End If

            Dim info2 As ColumnIndexInfo = sets.FindByColumn(field, IndexKind.Range)

            If info2 Is Nothing Then
                Return Nothing
            End If

            Dim lower As Object = RangeScalar(info2, value)

            If lower Is Nothing Then
                Return Nothing
            End If

            ' keep a superset here: the range borders are inclusive so that no matching
            ' row can be missed, the exact condition is checked again by the executor.
            If op = BinaryOp.Gt OrElse op = BinaryOp.Ge Then
                Return New Query With {
                    .search = Query.Type.ValueRange,
                    .field = field,
                    .value = RangePair(info2, lower, MaxValue(info2))
                }
            End If

            Return New Query With {
                .search = Query.Type.ValueRange,
                .field = field,
                .value = RangePair(info2, MinValue(info2), lower)
            }
        End Function

        Private Shared Function Flip(op As BinaryOp) As BinaryOp
            Select Case op
                Case BinaryOp.Lt : Return BinaryOp.Gt
                Case BinaryOp.Le : Return BinaryOp.Ge
                Case BinaryOp.Gt : Return BinaryOp.Lt
                Case BinaryOp.Ge : Return BinaryOp.Le
                Case Else : Return op
            End Select
        End Function

        ''' <summary>a bare, unqualified column reference usable for the search index</summary>
        Private Shared Function ColumnOf(expr As Expression) As String
            Dim col As IdentifierExpression = TryCast(expr, IdentifierExpression)

            If col Is Nothing OrElse col.Qualifier IsNot Nothing Then
                Return Nothing
            End If

            Return col.Name
        End Function

        Private Shared Function IsConst(expr As Expression) As Boolean
            If TypeOf expr Is LiteralExpression Then
                Return Not DirectCast(expr, LiteralExpression).IsNull
            End If

            Return False
        End Function

        Private Shared Function ConstValue(expr As Expression) As Object
            Return DirectCast(expr, LiteralExpression).Value
        End Function

        Private Shared Function RangeTypeOf(columnIndex As ColumnIndexInfo) As Type
            Select Case columnIndex.ValueType
                Case "Integer" : Return GetType(Integer)
                Case "Date" : Return GetType(Date)
                Case Else : Return GetType(Double)
            End Select
        End Function

        Private Shared Function RangeScalar(columnIndex As ColumnIndexInfo, value As Object) As Object
            Try
                If TypeOf value Is Date Then
                    Return If(columnIndex.ValueType = "Date", CObj(CDate(value)), Nothing)
                End If

                Dim n As Double = Convert.ToDouble(value)

                If columnIndex.ValueType = "Integer" Then
                    ' only whole numbers can be mapped onto an integer index
                    If Math.Round(n) <> n OrElse n < Integer.MinValue OrElse n > Integer.MaxValue Then
                        Return Nothing
                    End If

                    Return CInt(n)
                ElseIf columnIndex.ValueType = "Date" Then
                    If TypeOf value Is String Then
                        Dim d As Date

                        If Date.TryParse(CStr(value), d) Then
                            Return d
                        End If
                    End If

                    Return Nothing
                Else
                    Return n
                End If
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        ''' <summary>build the typed [min,max] array that the LINQ range index expects</summary>
        Private Shared Function RangePair(columnIndex As ColumnIndexInfo, min As Object, max As Object) As Object
            If RangeTypeOf(columnIndex) Is GetType(Integer) Then
                Return New Integer() {CInt(min), CInt(max)}
            ElseIf RangeTypeOf(columnIndex) Is GetType(Date) Then
                Return New Date() {CDate(min), CDate(max)}
            Else
                Return New Double() {CDbl(min), CDbl(max)}
            End If
        End Function

        Private Shared Function MinValue(columnIndex As ColumnIndexInfo) As Object
            If RangeTypeOf(columnIndex) Is GetType(Integer) Then
                Return Integer.MinValue
            ElseIf RangeTypeOf(columnIndex) Is GetType(Date) Then
                Return New Date(1900, 1, 1)
            Else
                Return -1.7E+308
            End If
        End Function

        Private Shared Function MaxValue(columnIndex As ColumnIndexInfo) As Object
            If RangeTypeOf(columnIndex) Is GetType(Integer) Then
                Return Integer.MaxValue
            ElseIf RangeTypeOf(columnIndex) Is GetType(Date) Then
                Return New Date(9999, 12, 31)
            Else
                Return 1.7E+308
            End If
        End Function
    End Class
End Namespace
