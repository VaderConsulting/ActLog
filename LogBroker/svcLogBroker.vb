Imports System.Xml.Serialization
Imports System.IO
Imports System.Security.Permissions

' Request full Trust permission
<Assembly: PermissionSetAttribute(SecurityAction.RequestMinimum, Name:="FullTrust")> 
Public Class LogBroker

    Public booDoStop As Boolean
    Public WithEvents timTimer As New Timers.Timer(60000)

    Protected Overrides Sub OnStart(ByVal args() As String)
        'DoStart()
    End Sub

    Protected Overrides Sub OnStop()
        DoStop()
    End Sub

    Private Sub DoStart()
        'SetupSharedFolder()
        WriteToEventLog("Log Broker Service started (Inside OnStart)", "Log Broker Service", EventLogEntryType.Information)
        timTimer.Enabled = True
        timTimer.AutoReset = True
        WriteToEventLog("Log Broker Service started (Leaving OnStart)", "Log Broker Service", EventLogEntryType.Information)
    End Sub

    Private Sub DoStop()
        timTimer.Enabled = False

        ' Flag to perform Stop
        booDoStop = True
    End Sub

    Private Sub DoTimerTick()
        Static intCount As Integer

        WriteToEventLog("Log Broker Service started (Inside DoTimerTick)", "Log Broker Service", EventLogEntryType.Information)

        intCount += 1

        If intCount >= My.Settings.FilecopyInterval Then ' 15 minutes by default
            StartOperations()
            intCount = 0
        End If
        WriteToEventLog("Log Broker Service started (Leaving DoTimerTick)", "Log Broker Service", EventLogEntryType.Information)
    End Sub

    ' Get the list of files to process
    Private Sub StartOperations()
        Dim strResults() As String
        Dim strFilename As String = ""

        ' Create an instance of the XmlSerializer specifying type.
        Dim Serializer As New XmlSerializer(GetType(clsUserInfo))

        ' Stop the Timer
        timTimer.Enabled = False

        strResults = IO.Directory.GetFiles(My.Settings.LogfileSourcePath, "*.xml")

        For i As Integer = 0 To UBound(strResults)
            Dim booSubmitResult As Boolean = False
            strFilename = strResults(i).ToString

            Dim oXMLDocument As New Xml.XmlDocument

            oXMLDocument.Load(strFilename)

            ' Now send this to the Web Service
            Dim oWebService As New steam.Service

            Try
                Dim strCompletePath As String = strFilename
                Dim strSourcePath As String = Microsoft.VisualBasic.Left(strCompletePath, InStrRev(strCompletePath, "\"))

                strFilename = Microsoft.VisualBasic.Right(strCompletePath, Len(strCompletePath) - InStrRev(strCompletePath, "\"))
                booSubmitResult = oWebService.SubmitLog(oXMLDocument.OuterXml, strFilename)

                If booSubmitResult Then
                    My.Computer.FileSystem.DeleteFile(strCompletePath)
                    ' Delete the file
                Else
                    ' Hmmm an error occured.
                End If
            Catch
                WriteToEventLog("Error submitting file (" & strFilename & ") to Activity Log Web Service", "Log Broker", EventLogEntryType.Warning, "Application")
            End Try
        Next

        ' Start the Timer
        timTimer.Enabled = True

    End Sub

    Private Sub SetupSharedFolder()
        Const FILE_SHARE = 0
        Const MAXIMUM_CONNECTIONS = 60000
        Dim strComputer As String = "."
        Dim objWMIService As Object
        Dim objNewShare As Object
        Dim errReturn As Integer

        Try
            ' Check for the existance of the Logs directory.  Create if not found.
            If Not My.Computer.FileSystem().DirectoryExists(My.Settings.LogfileSourcePath) Then
                My.Computer.FileSystem.CreateDirectory(My.Settings.LogfileSourcePath)
            End If

            objWMIService = GetObject("winmgmts:" _
                & "{impersonationLevel=impersonate}!\\" & strComputer & "\root\cimv2")

            objNewShare = objWMIService.Get("Win32_Share")

            errReturn = objNewShare.Create _
                (My.Settings.LogfileSourcePath, "ActivityLog$", FILE_SHARE, _
                    MAXIMUM_CONNECTIONS, "Temporary storage for Activity Logs.")
        Catch
        End Try
    End Sub

    Private Sub timTimer_Elapsed(ByVal sender As Object, ByVal e As System.Timers.ElapsedEventArgs) Handles timTimer.Elapsed
        DoTimerTick()
    End Sub

    Private Function WriteToEventLog(ByVal Entry As String, Optional ByVal AppName As String = "Log Broker Service", Optional ByVal EventType As EventLogEntryType = EventLogEntryType.Information, Optional ByVal LogName As String = "Application") As Boolean
        Dim objEventLog As New EventLog()

        Try
            Try
                'Register the App as an Event Source
                If Not Diagnostics.EventLog.SourceExists(AppName) Then
                    Diagnostics.EventLog.CreateEventSource(AppName, LogName)
                End If
            Catch
                ' An error occured
            End Try

            objEventLog.Source = AppName

            objEventLog.WriteEntry(Entry, EventType)
            Return True
        Catch Ex As Exception
            Return False

        End Try

    End Function

End Class
