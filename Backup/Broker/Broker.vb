Imports System.Xml.Serialization
Imports System.IO
Imports System.Security.Permissions

' Request full Trust permission
<Assembly: PermissionSetAttribute(SecurityAction.RequestMinimum, Name:="FullTrust")> 
Public Class Broker

    Public booDoStop As Boolean
    Public WithEvents timTimer As New Timers.Timer(My.Settings.TimerIntervalmS)

    Protected Overrides Sub OnStart(ByVal args() As String)
        DoStart()
    End Sub

    Protected Overrides Sub OnStop()
        DoStop()
    End Sub

    Private Sub DoStart()
        SetupSharedFolder()
        timTimer.Enabled = True
        timTimer.AutoReset = True
    End Sub

    Private Sub DoStop()
        timTimer.Enabled = False

        ' Flag to perform Stop
        booDoStop = True
    End Sub

    ' Launched by Timer_Elapsed
    Private Sub DoTimerTick()
        Static intCount As Integer

        intCount += 1

        If intCount >= CInt(My.Settings.FilecopyIntervalMinutes) Then ' 15 minutes by default
            StartOperations()
            intCount = 0
        End If
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

            oWebService.Url = My.Settings.WebServiceURL

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
                    WriteToEventLog("Unable to submit file (" & strFilename & ") to Activity Log Web Service", "Activity Log Broker", EventLogEntryType.Warning, "Application")
                End If
            Catch ex As Exception
                WriteToEventLog("Error submitting file (" & strFilename & ") to Activity Log Web Service (" & ex.Message & ")", "Activity Log Broker", EventLogEntryType.Error, "Application")
            End Try
        Next

        ' Start the Timer
        timTimer.Enabled = True
    End Sub

    ' Create ActivityLog$ share
    Private Sub SetupSharedFolder()
        Const FILE_SHARE = 0
        Dim strComputer As String = "."
        Dim objWMIService As Object
        Dim objNewShare As Object
        Dim errReturn As Integer

        Try
            ' Check for the existance of the Logs directory.  Create if not found.
            If Not My.Computer.FileSystem().DirectoryExists(My.Settings.LogfileSourcePath) Then
                My.Computer.FileSystem.CreateDirectory(My.Settings.LogfileSourcePath)
            End If

            objWMIService = GetObject("winmgmts:{impersonationLevel=impersonate}!\\" & strComputer & "\root\cimv2")

            objNewShare = objWMIService.Get("Win32_Share")

            errReturn = objNewShare.Create _
                (My.Settings.LogfileSourcePath, "ActivityLog$", FILE_SHARE, , "Temporary storage for Activity Logs.")
        Catch
            WriteToEventLog("Error creating ActivityLog$ file share", "Activity Log Broker", EventLogEntryType.Error)
        End Try
    End Sub

    Private Sub timTimer_Elapsed(ByVal sender As Object, ByVal e As System.Timers.ElapsedEventArgs) Handles timTimer.Elapsed
        DoTimerTick()
    End Sub

    ' Write to the event log
    Private Function WriteToEventLog(ByVal Entry As String, Optional ByVal AppName As String = "Activity Log Broker", Optional ByVal EventType As EventLogEntryType = EventLogEntryType.Information, Optional ByVal LogName As String = "Application") As Boolean
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
