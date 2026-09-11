Imports Microsoft.VisualBasic.Data.Repository

Namespace Storage

    ''' <summary>
    ''' runtime switches of the table storage layer. the jsonl layout is the
    ''' default, the legacy whole table json file is kept for compatibility.
    ''' </summary>
    Public Class StorageOptions

        ''' <summary>
        ''' how long the engine must stay idle before the pending WAL records are
        ''' merged back into the data file. &lt;= 0 disables the background merge.
        ''' </summary>
        Public Property MergeIdleSeconds As Integer = 30

        ''' <summary>
        ''' fsync the WAL on every write. false is much faster but only survives a
        ''' process crash; true also survives a power failure.
        ''' </summary>
        Public Property FsyncEachWrite As Boolean = False

        ''' <summary>merge the pending WAL once the operation count reaches this value</summary>
        Public Property MergeAfterOperations As Integer = 2000

        ''' <summary>write the store diagnostics(index rebuild, torn tail repair) to stderr</summary>
        Public Property Verbose As Boolean = False

        ''' <summary>create new tables as legacy single file json instead of the jsonl layout</summary>
        Public Property LegacyJson As Boolean = False

        ''' <summary>sparse line index granularity of the jsonl data file</summary>
        Public Property IndexGranularity As Integer = 1024

        ''' <summary>build the options object handed over to the jsonl store engine</summary>
        Public Function CreateJsonlOptions() As JsonlStoreOptions
            Return New JsonlStoreOptions With {
                .IndexGranularity = IndexGranularity,
                .FsyncEachWrite = FsyncEachWrite,
                .RepairTornTail = True
            }
        End Function
    End Class
End Namespace
