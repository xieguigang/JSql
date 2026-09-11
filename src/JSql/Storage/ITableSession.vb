Imports System.Collections.Generic

Namespace Storage

    ''' <summary>
    ''' a table storage session backed by one open data file. the schema lives in
    ''' its own file, the rows are appended / replaced / deleted line by line so
    ''' that the write ahead log only has to store the changed rows.
    ''' </summary>
    Public Interface ITableSession : Inherits IDisposable

        ReadOnly Property Layout As String
        ReadOnly Property Schema As TableSchema
        ReadOnly Property TableName As String
        ReadOnly Property SchemaFilePath As String
        ReadOnly Property DataFilePath As String
        ReadOnly Property LogFilePath As String

        ''' <summary>the current virtual row count of the data file</summary>
        ReadOnly Property LineCount As Long
        ''' <summary>the WAL operations that are not merged into the data file yet</summary>
        ReadOnly Property PendingOperations As Long
        ReadOnly Property PendingBufferedLines As Long
        ReadOnly Property HasPendingChanges As Boolean
        ReadOnly Property WalFileSize As Long
        ReadOnly Property DataFileSize As Long

        ''' <summary>
        ''' materialize every row, the pending changes are already applied. the row
        ''' order equals the line order of the data file.
        ''' </summary>
        Function ReadRows() As List(Of Dictionary(Of String, Object))

        ''' <summary>
        ''' synchronize the in memory rows with the storage: the difference is
        ''' translated into the fastest line level primitive (append / insert /
        ''' replace range).
        ''' </summary>
        Sub SyncRows(rows As List(Of Dictionary(Of String, Object)))

        ''' <summary>write the schema file when it actually changed</summary>
        Sub SaveSchema(schema As TableSchema)

        ''' <summary>flush the write ahead log</summary>
        Sub Flush()

        ''' <summary>merge the pending WAL operations back into the data file</summary>
        Sub Merge()

        Event Info(message As String)
    End Interface
End Namespace
