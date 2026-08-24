Imports System.Web
Imports System.Web.Services
Imports System.Web.Services.Protocols
Imports System.Xml.Serialization
Imports System.IO
Imports System.Diagnostics
Imports System.Data

<WebService(Namespace:="http://www.empired.com/webservices/")> _
<WebServiceBinding(ConformsTo:=WsiProfiles.BasicProfile1_1)> _
<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Public Class Service
    Inherits System.Web.Services.WebService

    Public mstrTempLogFilePath As String = ""
    Public mstrConnectionString As String = ""

    <WebMethod()> _
    Public Function TestService(ByVal strInput As String) As String
        Return "Input: " & strInput & " Output: " & Date.Now
    End Function

    ' This is the Webservice method to submit an activity log 
    <WebMethod()> _
    Public Function SubmitLog(ByVal strReceivedUserInfo As String, ByVal strFilename As String) As Boolean
        Dim rootWebConfig As System.Configuration.Configuration

        rootWebConfig = System.Web.Configuration.WebConfigurationManager.OpenWebConfiguration("/WebService")
        mstrTempLogFilePath = rootWebConfig.AppSettings.Settings.Item("LogDirectoryPath").Value.ToString
        If mstrTempLogFilePath.EndsWith("\") Then
            mstrTempLogFilePath = mstrTempLogFilePath & "temp"
        Else
            mstrTempLogFilePath = mstrTempLogFilePath & "\temp"
        End If

        'WriteToEventLog("Inside SubmitLog", "Activity Log Web Service", EventLogEntryType.Information, "Application")

        CreateTempLocation()

        If WriteLogToTemporaryLocation(mstrTempLogFilePath & "\" & strFilename, strReceivedUserInfo) Then
            Try
                If WriteUserInfoToDatabase(strFilename) Then
                    ' No errors
                    'WriteToEventLog("Correctly wrote User Info to Database", "Activity Log Web Service", EventLogEntryType.Information, "Application")
                    Return True
                Else
                    ' An error occured
                    'WriteToEventLog("Could not write User Info to Database", "Activity Log Web Service", EventLogEntryType.Warning, "Application")
                    Return False
                End If
            Catch ex As Exception
                'WriteToEventLog("Error Writing User Info to Database (" & ex.Message & ")", "Activity Log Web Service", EventLogEntryType.Error, "Application")
            End Try
        Else
            ' An error occured
            'WriteToEventLog("Could not write Log to temporary location", "Activity Log Web Service", EventLogEntryType.Warning, "Application")
            Return False
        End If

    End Function

    ' Create a temporary location to store log files
    Private Sub CreateTempLocation()
        Dim rootWebConfig As System.Configuration.Configuration

        rootWebConfig = System.Web.Configuration.WebConfigurationManager.OpenWebConfiguration("/WebService")

        Try
            My.Computer.FileSystem.CreateDirectory(rootWebConfig.AppSettings.Settings.Item("LogDirectoryPath").Value.ToString & "\temp")
        Catch
        End Try

        'WriteToEventLog("Leaving CreateTempLocation", "Activity Log Web Service", EventLogEntryType.Information, "Application")

    End Sub

    ' Write the log to a temporary location
    Private Function WriteLogToTemporaryLocation(ByVal strFilename As String, ByVal strContents As String) As Boolean
        Try
            ' Create a StreamWriter to write the file.
            Dim Writer As New StreamWriter(strFilename)

            Writer.Write(strContents)
            Writer.Close()
        Catch

            'WriteToEventLog("Leaving WriteLogToTemporaryLocation (Error)", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            Return False
        End Try

        'WriteToEventLog("Leaving WriteLogToTemporaryLocation", "Activity Log Web Service", EventLogEntryType.Information, "Application")

        Return True
    End Function

    ' Write the extracted user information to the database
    Private Function WriteUserInfoToDatabase(ByVal strFilename As String) As Boolean
        Dim rootWebConfig As System.Configuration.Configuration

        'WriteToEventLog("Inside WriteUserInfoToDatabase", "Activity Log Web Service", EventLogEntryType.Information, "Application")

        rootWebConfig = System.Web.Configuration.WebConfigurationManager.OpenWebConfiguration("/WebService")
        mstrConnectionString = rootWebConfig.AppSettings.Settings.Item("ConnectionString").Value.ToString
        Try
            'WriteToEventLog("WriteUserInfoToDatabase - 1", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Create an instance of the XmlSerializer specifying type.
            Dim Serializer As New XmlSerializer(GetType(clsUserInfo))

            'WriteToEventLog("WriteUserInfoToDatabase - 2", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Create a TextReader to read the file. 
            Dim fs As New FileStream(mstrTempLogFilePath & "\" & strFilename, FileMode.Open)
            Dim Reader As New StreamReader(fs)

            'WriteToEventLog("WriteUserInfoToDatabase - 3", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Declare an object variable of the type to be deserialized.
            Dim oUserInfo As New clsUserInfo

            'WriteToEventLog("WriteUserInfoToDatabase - 4", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Use the Deserialize method to restore the object's state.
            oUserInfo = CType(Serializer.Deserialize(Reader), clsUserInfo)

            'WriteToEventLog("WriteUserInfoToDatabase - 5", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Close the file.
            Reader.Close()

            Dim RemoteIPAddresses As String = ""

            If RemoteIPAddresses.Length > 0 Then
                'WriteToEventLog("WriteUserInfoToDatabase - 6", "Activity Log Web Service", EventLogEntryType.Information, "Application")
                For i As Integer = 0 To oUserInfo.IPAddresses.Length - 1
                    RemoteIPAddresses = RemoteIPAddresses & ";" & oUserInfo.IPAddresses(i).ToString
                Next

                If RemoteIPAddresses.StartsWith(";") Then RemoteIPAddresses = Right(RemoteIPAddresses, Len(RemoteIPAddresses) - 1)
                If RemoteIPAddresses.EndsWith(";") Then RemoteIPAddresses = Left(RemoteIPAddresses, Len(RemoteIPAddresses) - 1)
            End If


            'WriteToEventLog("WriteUserInfoToDatabase - 7", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            ' Now we have the data, push it to the database
            Dim strSQL As String = "INSERT INTO tblLogonEvents (" & _
                         "Username,Domainname,EventDescription," & _
                         "ComputerName,UTCLogonDateTime,UTCLogoffDateTime," & _
                         "LogonServer,SiteName,ModelNumber,OperatingSystemName," & _
                         "RemoteClientName,IPAddresses,RemoteSessionName," & _
                         "SerialNumber,Manufacturer,Version) " & _
                         "VALUES " & _
                         "(" & _
                         "'" & oUserInfo.Username.Trim & "'," & _
                         "'" & oUserInfo.Domainname.Trim & "'," & _
                         "'" & oUserInfo.EventDescription.Trim & "'," & _
                         "'" & oUserInfo.Computername.Trim & "'," & _
                         "'" & oUserInfo.UTCLogonDateTime.Trim & "'," & _
                         "'" & oUserInfo.UTCLogoffDateTime.Trim & "'," & _
                         "'" & oUserInfo.LogonServer.Trim & "'," & _
                         "'" & oUserInfo.Sitename.Trim & "'," & _
                         "'" & oUserInfo.ModelNumber.Trim & "'," & _
                         "'" & oUserInfo.OperatingSystemName.Trim & "'," & _
                         "'" & oUserInfo.RemoteClientName.Trim & "'," & _
                         "'" & RemoteIPAddresses.Trim & "'," & _
                         "'" & oUserInfo.RemoteSessionName.Trim & "'," & _
                         "'" & oUserInfo.SerialNumber.Trim & "'," & _
                         "'" & oUserInfo.Manufacturer.Trim & "'," & _
                         "'" & oUserInfo.Version.Trim & "'" & _
                         ")"

            'WriteToEventLog("WriteUserInfoToDatabase - 8", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            Dim oConnection As New SqlClient.SqlConnection(mstrConnectionString)
            oConnection.Open()
            'WriteToEventLog("WriteUserInfoToDatabase - 9", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            Dim oCommand As New SqlClient.SqlCommand(strSQL, oConnection)
            'WriteToEventLog("WriteUserInfoToDatabase - A", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            Dim RowCount As Integer = oCommand.ExecuteNonQuery()
            'WriteToEventLog("WriteUserInfoToDatabase - B", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            oConnection.Close()

            'WriteToEventLog("WriteUserInfoToDatabase - C", "Activity Log Web Service", EventLogEntryType.Information, "Application")
            My.Computer.FileSystem.DeleteFile(mstrTempLogFilePath & "\" & strFilename)
            Return True
        Catch ex As Exception
            'WriteToEventLog(ex.Message, "Activity Log Web Service", EventLogEntryType.Error, "Application")
            Return False
        End Try
    End Function

    ' Write to the application event log
    Private Function WriteToEventLog(ByVal Entry As String, Optional ByVal AppName As String = "Activity Log Web Service", Optional ByVal EventType As EventLogEntryType = EventLogEntryType.Information, Optional ByVal LogName As String = "Application") As Boolean
        Dim objEventLog As New EventLog()

        Try
            Try
                'Register the App as an Event Source
                If Not Diagnostics.EventLog.SourceExists(AppName) Then
                    Diagnostics.EventLog.CreateEventSource(AppName, LogName)
                End If
            Catch
                AppName = "ASP.NET"
            End Try

            objEventLog.Source = AppName

            'WriteEntry is overloaded; this is one of 10 ways to call it
            objEventLog.WriteEntry(Entry, EventType)
            Return True
        Catch Ex As Exception
            Return False

        End Try

    End Function

End Class
