Imports System.Xml
Imports System
Imports System.Security.Permissions
Imports System.Management
Imports System.Xml.Serialization
Imports System.IO
Imports System.Diagnostics

' Request full Trust permission
<Assembly: PermissionSetAttribute(SecurityAction.RequestMinimum, Name:="FullTrust")> 
Public Class svcActLogClient

    Private Declare Unicode Function ConvertStringSidToSidW Lib "advapi32.dll" (ByVal StringSID As String, ByRef SID As IntPtr) As Boolean
    Private Declare Unicode Function LookupAccountSidW Lib "advapi32.dll" (ByVal lpSystemName As String, ByVal SID As IntPtr, ByVal Name As Text.StringBuilder, ByRef cbName As Long, ByVal DomainName As Text.StringBuilder, ByRef cbDomainName As Long, ByRef psUse As Integer) As Boolean

    Public CurrentUserlist As clsUserInfoList
    Public PreviousUserList As clsUserInfoList
    Dim strOperatingSystem As String = My.Computer.Info.OSFullName
    Dim strComputername As String = My.Computer.Name
    Dim strManufacturer As String = ""
    Dim strModelNumber As String = ""
    Dim strSerialNumber As String = ""
    Dim strSitename As String = ""
    Dim booOKToCreateLogFile As Boolean = True
    Public lngTimezoneOffsetMinutes As Long
    Public UserInfo() As clsUserInfo
    Public colLoggedOnUserList As New clsUserInfoList
    Public colLoggedOffUserList As New clsUserInfoList
    Public colExcludedUserList As New Collections.Specialized.StringCollection
    Public WithEvents timTimer As New Timers.Timer(60000)
    Private WithEvents SecurityLog As EventLog
    Private bActivityLogShareError As Boolean = False
    Private bRootDSEError As Boolean = False

    ' Called by Service Control Manager to start the Service
    Protected Overrides Sub OnStart(ByVal args() As String)
        DoStart()
    End Sub

    ' Called by Service Control Manager to stop the Service
    Protected Overrides Sub OnStop()
        DoStop()
    End Sub

    Private Sub DoStart()

        GetComputerInfo()

        SecurityLog = New EventLog()
        SecurityLog.Log = "Security"
        SecurityLog.EnableRaisingEvents = True
        AddHandler SecurityLog.EntryWritten, AddressOf EntryWritten

        GetExclusionList()

        timTimer.Enabled = True
        timTimer.AutoReset = True

    End Sub

    ' Determine computer info (Name, Site, Timezone, Serial, Manufacturer, Model)
    Private Sub GetComputerInfo()
        Dim strErrorLogMessage As String = ""

        strOperatingSystem = My.Computer.Info.OSFullName

        Try
            strComputername = My.Computer.Name
        Catch
            strErrorLogMessage = "Failed to determine the computername for this computer." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "This may indicate a problem with the Network and\or the DNS configuration of this computer, and should be resolved immediately." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer may not be replicated to the Activity Log system." & vbCrLf
            WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
            strComputername = "[UNKNOWN]"
        End Try

        Try
            strSitename = System.DirectoryServices.ActiveDirectory.ActiveDirectorySite.GetComputerSite.Name.ToString
        Catch
            strErrorLogMessage = "Failed to determine the Active Directory Site for this computer." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "This may indicate a problem with the Network, DNS or IP configuration of this computer, and should be resolved immediately." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer may not be replicated to the Activity Log system." & vbCrLf
            WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
            strSitename = "[UNKNOWN]"
        End Try

        ' Get TimeZone offset
        lngTimezoneOffsetMinutes = DateDiff(DateInterval.Minute, My.Computer.Clock.GmtTime, My.Computer.Clock.LocalTime)

        GetPCSerial()
        GetPCManufacturerAndModel()
    End Sub

    ' Get the list of users we don't want to know about
    Private Sub GetExclusionList()
        Dim strExclusionList() As String

        strExclusionList = Split(My.Settings.ExcludedUserListSuccess, ";")

        For i As Integer = 0 To strExclusionList.Length - 1
            colExcludedUserList.Add(strExclusionList(i).ToUpper)
        Next
    End Sub

    ' Determine User Information (if not using event log)
    Private Sub GetUserInfo()

        timTimer.Enabled = False

        ' If configured to NOT use the Event Log to determine who is logged on...
        If Not My.Settings.UseEventLog Then
            GetCurrentUserList()
            CompareLists()
            PreviousUserList = CurrentUserlist
        End If

        timTimer.Enabled = True
    End Sub

    ' Determines who is logged on by listing running processes
    Private Sub GetCurrentUserList()
        Dim oManagementClass As New ManagementClass("Win32_Process")
        Dim oManagementObjectColl As ManagementObjectCollection = oManagementClass.GetInstances
        Dim oManagementObject As ManagementObject
        Dim processDomain As String = ""
        Dim processUser As String = ""
        Dim strSID As String = ""
        Dim DomainAndUsername As String = ""
        Dim oNewUser As New clsUserInfo

        CurrentUserlist = New clsUserInfoList
        For Each oManagementObject In oManagementObjectColl

            Dim oProcess As New ROOT.CIMV2.Process(oManagementObject)
            oProcess.GetOwner(processDomain, processUser)


            DomainAndUsername = processDomain & "\" & processUser

            ' Get the user's SID
            oProcess.GetOwnerSid(strSID)

            ' Set specifics
            oNewUser.Username = processUser
            oNewUser.Computername = strComputername
            oNewUser.Domainname = processDomain
            oNewUser.OperatingSystemName = strOperatingSystem
            oNewUser.ModelNumber = strModelNumber
            oNewUser.SerialNumber = strSerialNumber
            oNewUser.Sitename = strSitename

            ' Set default event.  This may change when we determine differences
            oNewUser.EventDescription = "Application Startup"

            ' Determine details
            oNewUser.DetermineLogonData(strSID)
            oNewUser.DetermineLastLogonDateTime()

            ' Save Data
            If Not CurrentUserlist.Contains(oNewUser) Then
                CurrentUserlist.Add(oNewUser.Clone)
            End If

            ' Clear previous values
            oNewUser.Clear()
            DomainAndUsername = ""
            strSID = ""
        Next
    End Sub

    ' Compare the current and previous user lists to determine who has logged on or off
    Private Sub CompareLists()
        Dim oUser As clsUserInfo
        Dim strErrorLogMessage As String = ""

        If PreviousUserList Is Nothing Then
            ' No previous users
            PreviousUserList = New clsUserInfoList
            For i As Integer = 0 To CurrentUserlist.Count - 1
                oUser = CurrentUserlist.Item(i)
                oUser.EventDescription = "UserLoggedOnAtServiceStartup"
                colLoggedOnUserList.Add(oUser)
                If booOKToCreateLogFile Then
                    oUser.Serialize()
                Else
                    strErrorLogMessage = "Unable to add User " & oUser.Username & " to Logged On User List due to insufficient diskspace.  This information has been lost."
                    WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
                End If
            Next
        Else
            For i As Integer = 0 To CurrentUserlist.Count - 1
                oUser = CurrentUserlist.Item(i)

                If Not PreviousUserList.Contains(oUser) Then
                    oUser.EventDescription = "Logon"
                    colLoggedOnUserList.Add(oUser)
                    If booOKToCreateLogFile Then
                        oUser.Serialize()
                    Else
                        strErrorLogMessage = "Unable to add User " & oUser.Username & " to Logged On User List due to insufficient diskspace.  This information has been lost."
                        WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
                    End If
                End If
            Next

            For i As Integer = 0 To PreviousUserList.Count - 1
                oUser = PreviousUserList.Item(i)

                If Not CurrentUserlist.Contains(oUser) Then
                    oUser.EventDescription = "Logoff"
                    oUser.DetermineLastLogoffDateTime()
                    colLoggedOffUserList.Add(oUser)
                    If booOKToCreateLogFile Then
                        oUser.Serialize()
                    Else
                        strErrorLogMessage = "Unable to add User " & oUser.Username & " to Logged Off User List due to insufficient diskspace.  This information has been lost."
                        WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
                    End If
                End If
            Next
        End If
    End Sub

    Private Sub DoStop()

    End Sub

    ' Use WMI to determine PC Serial Number
    Private Sub GetPCSerial()

        Try
            Dim mosSearcher As New System.Management.ManagementObjectSearcher(My.Settings.PCSerialWMIQuery)
            Dim moWin32_SystemEnclosure As System.Management.ManagementObject
            For Each moWin32_SystemEnclosure In mosSearcher.Get()
                strSerialNumber = moWin32_SystemEnclosure.Item(My.Settings.PCSerialWMIProperty)
            Next
        Catch
            strSerialNumber = ""
        End Try
    End Sub

    ' Use WMI to determine PC Manufacturer and Model Number
    Private Sub GetPCManufacturerAndModel()

        Try
            Dim mosSearcher As New System.Management.ManagementObjectSearcher(My.Settings.PCModelWMIQuery)
            Dim moWin32_SystemEnclosure As System.Management.ManagementObject
            For Each moWin32_SystemEnclosure In mosSearcher.Get()
                strManufacturer = moWin32_SystemEnclosure.Item(My.Settings.PCManufacturerWMIProperty)
                strModelNumber = moWin32_SystemEnclosure.Item(My.Settings.PCModelWMIProperty)
            Next
        Catch
            strManufacturer = ""
            strModelNumber = ""
        End Try
    End Sub

    ' Get each logfile and process it (Step 1 of the copy process)
    Private Sub ProcessLogfiles()
        Dim strResults() As String
        Dim strDCName As String = ""
        Dim strDestination As String = ""
        Dim strCompletePath As String = ""
        Dim strFilename As String = ""
        Dim strSourcePath As String = ""
        Dim booSuccess As Boolean = False
        Dim strErrorLogMessage As String = ""

        ' Create an instance of the XmlSerializer specifying type.
        Dim Serializer As New XmlSerializer(GetType(clsUserInfo))

        ' Get a list of files to copy
        strResults = IO.Directory.GetFiles(My.Application.Info.DirectoryPath & "\Logs", "*.xml")

        ' For each of these files...
        For i As Integer = 0 To UBound(strResults)
            strCompletePath = strResults(i).ToString
            strSourcePath = Microsoft.VisualBasic.Left(strCompletePath, InStrRev(strCompletePath, "\"))
            strFilename = Microsoft.VisualBasic.Right(strCompletePath, Len(strCompletePath) - InStrRev(strCompletePath, "\"))
            strErrorLogMessage = ""

            ' Create a TextReader to read the file. 
            Dim fs As New FileStream(strCompletePath, FileMode.Open)
            Dim reader As New StreamReader(fs)

            ' Declare an object variable of the type to be deserialized.
            Dim oDeserializedObject As clsUserInfo

            ' Use the Deserialize method to restore the object's state.
            oDeserializedObject = CType(Serializer.Deserialize(reader), clsUserInfo)

            ' Close the file.
            reader.Close()

            ' Now send this to the Server
            booSuccess = CopyLogToServer(strFilename, strSourcePath)

            If Not booSuccess Then
                If Not bRootDSEError And Not bActivityLogShareError Then
                    ' A valid location to send the activity log file could not be found
                    strErrorLogMessage = "A valid ActivityLog$ share could not be found on any Site Server found within Active Directory for this Site.  Review previous warning messages to determine the cause for this error." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, one or more Activity Logs for this computer cannot be replicated to the Activity Log system.  If this computer is a laptop, and is not connected to the network, this error can be ignored." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will attempt to contact a Site Server again in one minute." & vbCrLf
                    WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Error)
                End If
            End If
        Next

    End Sub

    ' Copy log files to one of the Servers found in AD (Step 2 of the copy process, called by ProcessLogFiles)
    Private Function CopyLogToServer(ByVal strFilename As String, ByVal strSourcePath As String) As Boolean '  TRUE = SUCCESS
        Dim strSiteRDN As String = ""
        Dim objRootDSE As Object
        Dim strConfigurationNC As String = ""
        Dim strServersPath As String = ""
        Dim objServersContainer As Object
        Dim objServer As Object
        Dim colServers As New Collections.Specialized.StringCollection
        Dim strServername As String = ""
        Dim strDestination As String = ""
        Dim strErrorLogMessage As String = ""
        Dim booPreferredServerCopySuccess As Boolean = False

        strSiteRDN = "cn=" & strSitename

        If strSitename = "" Then
            strSitename = "[LOCAL LOGON]"
        End If

        ' Work with preferred server first.
        If My.Settings.PreferredServer.Trim <> "" Then
            strServername = My.Settings.PreferredServer
            booPreferredServerCopySuccess = DoFileCopy(strServername, strDestination, strSourcePath, strFilename)
        End If

        ' Copy to Preferred Server was a success.
        If booPreferredServerCopySuccess Then
            ' Set Error state to false
            bActivityLogShareError = False
            Return True
        Else
            Try
                objRootDSE = GetObject("LDAP://RootDSE")
                strConfigurationNC = objRootDSE.Get("configurationNamingContext")

                strServersPath = "LDAP://cn=Servers," & strSiteRDN & ",cn=Sites," & strConfigurationNC
                objServersContainer = GetObject(strServersPath)

                For Each objServer In objServersContainer
                    strServername = Replace(objServer.Name.ToString.ToLower, "cn=", "")
                    strErrorLogMessage = ""
                    If DoFileCopy(strServername, strDestination, strSourcePath, strFilename) Then
                        strErrorLogMessage = "'Preferred Server' set to " & strServername & "."
                        WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Information)
                        ' Set Error state to false
                        bActivityLogShareError = False
                        Return True
                    End If
                Next
            Catch
                If Not bRootDSEError Then
                    strErrorLogMessage = "Failed to determine the RootDSE for this computer." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "This indicates a problem with the Network and\or Active Directory configuration of this computer, and should be resolved immediately." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer cannot be replicated to the Activity Log system." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "The Activity Log Client cannot proceed." & vbCrLf
                    WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Error)
                    bRootDSEError = True
                End If
                Return False
            End Try

            If Not bActivityLogShareError Then
                ' If we get this far, a valid ActivityLog$ share could not be found!!
                strErrorLogMessage = "A valid ActivityLog$ share could not be found." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "This may indicate one of the following:" & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "1.  There are no Activity Log Servers configured." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "2.  There are no Servers listed in Active Directory for this site (" & strSitename & ")." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "3.  Communication problems are preventing this computer from contacting any Activity Log Server." & vbCrLf & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer cannot be replicated to the Activity Log system.  If this computer is a laptop, and is not connected to the network, this error can be ignored." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will attempt to contact a Site Server again in one minute." & vbCrLf
                WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
                bActivityLogShareError = True
            End If
            Return False
        End If

    End Function

    ' Copy log file to specific location (i.e. specified Server) (Step 3 of the copy process, called by CopyLogToServer)
    Private Function DoFileCopy(ByVal strServername As String, ByVal strDestination As String, ByVal strSourcePath As String, ByVal strFilename As String) As Boolean
        Dim strErrorLogMessage As String = ""

        Try
            If My.Computer.Network.Ping(strServername) Then
                ' Attempt to copy the log file to this server

                strDestination = "\\" & strServername & "\ActivityLog$"
                Try
                    My.Computer.Network.UploadFile(strSourcePath & strFilename, strDestination & "\" & strFilename)
                    My.Computer.FileSystem.DeleteFile(strSourcePath & strFilename)
                    strErrorLogMessage = "Successfully copied Activity logfile " & strFilename & " to " & strServername & "."
                    WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Information)
                    My.Settings.PreferredServer = strServername

                    ' SUCCESS!
                    Return True
                Catch ex As Exception
                    ' No, this did not work
                    ' Failed to copy logfile
                    strErrorLogMessage = "Failed to copy logfile to " & strServername & "." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "This may indicate one of the following:" & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "1.  The " & strServername & "\ActivityLog$ Share could not be found" & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "2.  The " & strServername & "\ActivityLog$ Share does not have the correct permissions (Domain Computers: FULL)" & vbCrLf & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer may not be replicated to the Activity Log system." & vbCrLf
                    strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will now attempt to contact any remaining Site Servers." & vbCrLf
                    WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
                End Try
            Else
                ' Server is not contactable
                strErrorLogMessage = "The Server " & strServername & " is not contactable." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "This may indicate one of the following:" & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "1.  " & strServername & " is not on the network" & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "2.  " & strServername & " is behind a firewall through which this computer has no accesss" & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "3.  Intermittent communication problems are sometimes preventing this computer from accessing " & strServername & vbCrLf & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer may not be replicated to the Activity Log system." & vbCrLf
                strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will now attempt to contact any remaining Site Servers." & vbCrLf
                WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
            End If
        Catch
            ' A server by this name could not be resolved.
            ' This Server was not resolved to an IP Address
            strErrorLogMessage = "The Server " & strServername & " was not resolved to an IP Address." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "This may indicate one of the following:" & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "1.  " & strServername & " is not on the network (and has not been for a long period)" & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "2.  " & strServername & " is incorrectly listed in AD Sites and Services as a server for this computer's AD Site, and should be removed" & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "3.  Communication problems are preventing this computer from resolving an address to " & strServername & vbCrLf & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, Activity Logs for this computer may not be replicated to the Activity Log system." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will now attempt to contact any remaining Site Servers." & vbCrLf
            WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
        End Try

        Return False
    End Function

    ' Write to the Application Event Log
    Private Function WriteToEventLog(ByVal Entry As String, Optional ByVal AppName As String = "Activity Log Client", Optional ByVal EventType As EventLogEntryType = EventLogEntryType.Information, Optional ByVal LogName As String = "Application") As Boolean
        Dim objEventLog As New EventLog()

        Try
            'Register the App as an Event Source
            If Not Diagnostics.EventLog.SourceExists(AppName) Then
                Diagnostics.EventLog.CreateEventSource(AppName, LogName)
            End If

            objEventLog.Source = AppName

            'WriteEntry is overloaded; this is one of 10 ways to call it
            objEventLog.WriteEntry(Entry, EventType)
            Return True
        Catch Ex As Exception
            Return False

        End Try

    End Function

    ' Used to get User information periodically, as well as to process any log files
    Private Sub timTimer_Elapsed(ByVal sender As Object, ByVal e As System.Timers.ElapsedEventArgs) Handles timTimer.Elapsed
        Dim strErrorLogMessage As String = ""

        ' Check if there is > [MinimumDiskSpaceMbBeforeLogging] MB free on the system drive
        If My.Computer.FileSystem.GetDriveInfo("C:").AvailableFreeSpace > (My.Settings.MinimumDiskSpaceMbBeforeLogging * 1048576) Then
            booOKToCreateLogFile = True
        Else
            booOKToCreateLogFile = False
        End If

        If CheckNetworkConnection() Then
            GetUserInfo()
        Else
            strErrorLogMessage = "There is an issue preventing this computer from accessing the Network." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "Until this problem is corrected, one or more Activity Logs for this computer cannot be replicated to the Activity Log system.  If this computer is a laptop, and is not connected to the network, this error can be ignored." & vbCrLf
            strErrorLogMessage = strErrorLogMessage & "The Activity Log Client will attempt to contact a Site Server again in one minute." & vbCrLf
            WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
        End If

        ProcessLogfiles()

    End Sub

    ' Check the state of the Network connection
    Function CheckNetworkConnection() As Boolean
        If My.Computer.Network.IsAvailable Then
            Return True
        End If
    End Function

    ' Called when an entry is written to the Event Log.  
    ' This is where all of the Event Log information is interpreted
    Private Sub EntryWritten(ByVal sender As Object, ByVal LogEntry As System.Diagnostics.EntryWrittenEventArgs)
        Dim strEntryType As String = ""
        Dim strErrorLogMessage As String = ""
        Dim oUser As New clsUserInfo
        Dim strErrorLogMessageParts() As String
        Dim mstrUsername As String = ""
        Dim mstrDomainname As String = ""
        Dim mstrDescription As String = ""
        Dim mstrLogonTypeID As String = ""
        Dim mstrLogonType As String = ""
        Dim mstrEventID As String = ""
        'Dim mstrLogonServer As String = ""
        Dim mstrLocalLogonDateTime As String = ""
        Dim mstrLocalLogoffDateTime As String = ""
        Dim mstrRemoteIPAddress As String = ""
        Dim mstrRemoteWorkstationName As String = ""
        Dim mstrRemoteSessionName As String = ""
        Dim bWriteLog As Boolean = False
        Dim strOffsetSign As String = "+"
        Dim mstrLogonID As String
        Dim strErrorSection As String = "0"

        On Error GoTo WriteErrorInfo

        Select Case LogEntry.Entry.EntryType
            Case EventLogEntryType.Information
                strEntryType = "Information"
            Case EventLogEntryType.Error
                strEntryType = "Error"
            Case EventLogEntryType.SuccessAudit
                strEntryType = "Success"
            Case EventLogEntryType.FailureAudit
                strEntryType = "Failure"
            Case EventLogEntryType.Warning
                strEntryType = "Warning"
        End Select

        strErrorLogMessage = LogEntry.Entry.Message

        'Turn the entry into something we know and understand
        strErrorLogMessage = Trim(Replace(strErrorLogMessage, vbTab, ""))
        strErrorLogMessage = Trim(Replace(strErrorLogMessage, Chr(10), ""))
        strErrorLogMessage = Trim(Replace(strErrorLogMessage, Chr(13) & Chr(13), "__"))
        strErrorLogMessage = Trim(Replace(strErrorLogMessage, "__", Chr(13)))

        strErrorLogMessageParts = Split(strErrorLogMessage, Chr(13))

        mstrEventID = LogEntry.Entry.InstanceId.ToString

        strErrorSection = "EntryWritten:1"

        Select Case Trim(mstrEventID)
            Case "528", "4624" ' Logon
                strErrorSection = "EntryWritten:2"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(2), "Domain:", "")
                mstrLogonID = Replace(strErrorLogMessageParts(3), "Logon ID:", "")
                mstrLogonTypeID = Replace(strErrorLogMessageParts(4), "Logon Type:", "")
                On Error Resume Next
                mstrRemoteWorkstationName = Replace(strErrorLogMessageParts(7), "Workstation Name:", "")
                If UBound(strErrorLogMessageParts) >= 14 Then mstrRemoteIPAddress = Replace(strErrorLogMessageParts(14), "Source Network Address:", "")
                On Error GoTo WriteErrorInfo
                bWriteLog = True
                strErrorSection = "EntryWritten:3"

                Select Case Trim(mstrLogonTypeID)
                    Case "0"
                        mstrLogonType = "SYSTEM"
                        mstrDescription = "Successful Logon by the SYSTEM Account (Event ID " & Trim(mstrEventID) & ")"
                    Case "2"
                        mstrLogonType = "Interactive"
                        mstrDescription = "Successful Logon to local console or with RUNAS (Event ID " & Trim(mstrEventID) & ")"
                    Case "3"
                        mstrLogonType = "Network"
                        mstrDescription = "Successful Network connection to a shared resource (Event ID " & Trim(mstrEventID) & ")"
                    Case "4"
                        mstrLogonType = "Batch"
                        mstrDescription = "Successful Logon by a Batch Process or Scheduled Task (Event ID " & Trim(mstrEventID) & ")"
                    Case "5"
                        mstrLogonType = "Service"
                        mstrDescription = "Successful Logon by a Service (Event ID " & Trim(mstrEventID) & ")"
                    Case "6"
                        mstrLogonType = "Proxy"
                        mstrDescription = "Successful Logon.  LogonType=6 (Proxy) (Event ID " & Trim(mstrEventID) & ")"
                    Case "7"
                        mstrLogonType = "Unlock"
                        mstrDescription = "Successful Workstation Unlock (Event ID " & Trim(mstrEventID) & ")"
                    Case "8"
                        mstrLogonType = "NetworkClearText"
                        mstrDescription = "Successful Logon via ASP script or IIS Basic Authentication (Event ID " & Trim(mstrEventID) & ")"
                    Case "9"
                        mstrLogonType = "NewCredentials"
                        mstrDescription = "Successful Logon with RUNAS [forward slash] netonly (Event ID " & Trim(mstrEventID) & ")"
                    Case "10"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Successful Logon via Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "11"
                        mstrLogonType = "CachedInteractive"
                        mstrDescription = "Successful Logon with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                    Case "12"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Successful Logon via Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "13"
                        mstrLogonType = "CachedUnlock"
                        mstrDescription = "Successful Workstation Unlock with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                End Select
            Case "529", "4625"
                strErrorSection = "EntryWritten:4"
                mstrDescription = "Logon failure - Unknown username or bad password (Event ID " & Trim(mstrEventID) & ")"
                mstrUsername = Replace(strErrorLogMessageParts(2), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(3), "Domain:", "")
                mstrLogonTypeID = Replace(strErrorLogMessageParts(4), "Logon Type:", "")
                'On Error Resume Next
                If UBound(strErrorLogMessageParts) >= 13 Then mstrRemoteIPAddress = Replace(strErrorLogMessageParts(13), "Source Network Address:", "")
                'On Error GoTo WriteErrorInfo
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
                strErrorSection = "EntryWritten:5"
                Select Case Trim(mstrLogonTypeID)
                    Case "0"
                        mstrLogonType = "SYSTEM"
                        mstrDescription = "Logon failure by the SYSTEM Account (Event ID " & Trim(mstrEventID) & ")"
                    Case "2"
                        mstrLogonType = "Interactive"
                        mstrDescription = "Logon failure (Unknown username or bad password) from local console or with RUNAS (Event ID " & Trim(mstrEventID) & ")"
                    Case "3"
                        mstrLogonType = "Network"
                        mstrDescription = "Logon failure (Unknown username or bad password) disconnecting from a shared resource (Event ID " & Trim(mstrEventID) & ")"
                    Case "4"
                        mstrLogonType = "Batch"
                        mstrDescription = "Logon failure (Unknown username or bad password) by a Batch Process or Scheduled Task (Event ID " & Trim(mstrEventID) & ")"
                    Case "5"
                        mstrLogonType = "Service"
                        mstrDescription = "Logon failure (Unknown username or bad password) by a Service (Event ID " & Trim(mstrEventID) & ")"
                    Case "6"
                        mstrLogonType = "Proxy"
                        mstrDescription = "Logon failure (Unknown username or bad password).  LogonType=6 (Proxy) (Event ID " & Trim(mstrEventID) & ")"
                    Case "7"
                        mstrLogonType = "Unlock"
                        mstrDescription = "Logon failure (Unknown username or bad password) during Workstation Unlock (Event ID " & Trim(mstrEventID) & ")"
                    Case "8"
                        mstrLogonType = "NetworkClearText"
                        mstrDescription = "Logon failure (Unknown username or bad password) via ASP script or IIS Basic Authentication (Event ID " & Trim(mstrEventID) & ")"
                    Case "9"
                        mstrLogonType = "NewCredentials"
                        mstrDescription = "Logon failure (Unknown username or bad password) with RUNAS [forward slash] netonly (Event ID " & Trim(mstrEventID) & ")"
                    Case "10", "12"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Logon failure (Unknown username or bad password) from Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "11"
                        mstrLogonType = "CachedInteractive"
                        mstrDescription = "Logon failure (Unknown username or bad password) with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                    Case "13"
                        mstrLogonType = "CachedUnlock"
                        mstrDescription = "Logon failure (Unknown username or bad password) during Workstation Unlock with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                End Select
            Case "530", "4626"
                strErrorSection = "EntryWritten:6"
                mstrDescription = "Logon failure - Logon time restriction violation (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "531", "4627"
                strErrorSection = "EntryWritten:7"
                mstrDescription = "Logon failure - Account currently disabled (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "532", "4628"
                strErrorSection = "EntryWritten:8"
                mstrDescription = "Logon failure - Account expired (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "533", "4629"
                strErrorSection = "EntryWritten:9"
                mstrDescription = "Logon failure - Account not allowed to logon to this workstation (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "534", "4630"
                strErrorSection = "EntryWritten:10"
                mstrDescription = "Logon failure - Inadequate rights (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "535", "4631"
                strErrorSection = "EntryWritten:11"
                mstrDescription = "Logon failure - Password expired (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "536", "4632"
                strErrorSection = "EntryWritten:12"
                mstrDescription = "Logon failure - Netlogon Service not running (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "537", "4633"
                strErrorSection = "EntryWritten:13"
                mstrDescription = "Logon failure - Unexpected error (Event ID " & Trim(mstrEventID) & ")"
                mstrLogonTypeID = Replace(strErrorLogMessageParts(4), "Logon Type:", "")
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
                strErrorSection = "EntryWritten:14"
                Select Case Trim(mstrLogonTypeID)
                    Case "0"
                        mstrLogonType = "SYSTEM"
                        mstrDescription = "Logon failure by the SYSTEM Account (Event ID " & Trim(mstrEventID) & ")"
                    Case "2"
                        mstrLogonType = "Interactive"
                        mstrDescription = "Logon failure from local console or with RUNAS (Event ID " & Trim(mstrEventID) & ")"
                    Case "3"
                        mstrLogonType = "Network"
                        mstrDescription = "Logon failure disconnecting from a shared resource (Event ID " & Trim(mstrEventID) & ")"
                    Case "4"
                        mstrLogonType = "Batch"
                        mstrDescription = "Logon failure by a Batch Process or Scheduled Task (Event ID " & Trim(mstrEventID) & ")"
                    Case "5"
                        mstrLogonType = "Service"
                        mstrDescription = "Logon failure by a Service (Event ID " & Trim(mstrEventID) & ")"
                    Case "6"
                        mstrLogonType = "Proxy"
                        mstrDescription = "Logon failure.  LogonType=6 (Proxy) (Event ID " & Trim(mstrEventID) & ")"
                    Case "7"
                        mstrLogonType = "Unlock"
                        mstrDescription = "Logon failure during Workstation Unlock (Event ID " & Trim(mstrEventID) & ")"
                    Case "8"
                        mstrLogonType = "NetworkClearText"
                        mstrDescription = "Logon failure via ASP script or IIS Basic Authentication (Event ID " & Trim(mstrEventID) & ")"
                    Case "9"
                        mstrLogonType = "NewCredentials"
                        mstrDescription = "Logon failure with RUNAS netonly (Event ID " & Trim(mstrEventID) & ")"
                    Case "10", "12"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Logon failure from Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "11"
                        mstrLogonType = "CachedInteractive"
                        mstrDescription = "Logon failure with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                    Case "13"
                        mstrLogonType = "CachedUnlock"
                        mstrDescription = "Logon failure during Workstation Unlock with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                End Select
            Case "538", "4634" ' Network Logoff
                strErrorSection = "EntryWritten:15"
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(2), "Domain:", "")
                mstrLogonID = Replace(strErrorLogMessageParts(3), "Logon ID:", "")
                mstrLogonTypeID = Replace(strErrorLogMessageParts(4), "Logon Type:", "")
                mstrLocalLogoffDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
                strErrorSection = "EntryWritten:16"
                Select Case Trim(mstrLogonTypeID)
                    Case "0"
                        mstrLogonType = "SYSTEM"
                        mstrDescription = "Successful Logoff by the SYSTEM Account (Event ID " & Trim(mstrEventID) & ")"
                    Case "2"
                        mstrLogonType = "Interactive"
                        mstrDescription = "Successful Logoff from local console or with RUNAS (Event ID " & Trim(mstrEventID) & ")"
                    Case "3"
                        mstrLogonType = "Network"
                        mstrDescription = "Successful Network disconnection from a shared resource (Event ID " & Trim(mstrEventID) & ")"
                        If My.Settings.LogEvent538Type3 = False Then Exit Sub
                    Case "4"
                        mstrLogonType = "Batch"
                        mstrDescription = "Successful Logoff by a Batch Process or Scheduled Task (Event ID " & Trim(mstrEventID) & ")"
                    Case "5"
                        mstrLogonType = "Service"
                        mstrDescription = "Successful Logoff by a Service (Event ID " & Trim(mstrEventID) & ")"
                    Case "6"
                        mstrLogonType = "Proxy"
                        mstrDescription = "Successful Logoff.  LogonType=6 (Proxy) (Event ID " & Trim(mstrEventID) & ")"
                    Case "7"
                        mstrLogonType = "Lock"
                        mstrDescription = "Successful Workstation Lock (Event ID " & Trim(mstrEventID) & ")"
                        ' NOTE:  Windows logs this event at the same time the workstation is unlocked!!! ???
                        ' TODO:  Look to see if the workstation is locked, and log this instead.  (MAYBE)
                    Case "8"
                        mstrLogonType = "NetworkClearText"
                        mstrDescription = "Successful Logoff via ASP script or IIS Basic Authentication (Event ID " & Trim(mstrEventID) & ")"
                    Case "9"
                        mstrLogonType = "NewCredentials"
                        mstrDescription = "Successful Logoff with RUNAS netonly (Event ID " & Trim(mstrEventID) & ")"
                    Case "10", "12"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Successful Logoff from Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "11"
                        mstrLogonType = "CachedInteractive"
                        mstrDescription = "Successful Logoff with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                    Case "13"
                        mstrLogonType = "CachedUnlock"
                        mstrDescription = "Successful Workstation Unlock with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                End Select
            Case "539", "4635"
                strErrorSection = "EntryWritten:17"
                mstrDescription = "Logon failure - Account locked out (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "540", "4636" ' Network Logon
                strErrorSection = "EntryWritten:18"
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(2), "Domain:", "")
                mstrLogonID = Replace(strErrorLogMessageParts(3), "Logon ID:", "")
                mstrLogonTypeID = Replace(strErrorLogMessageParts(4), "Logon Type:", "")
                If UBound(strErrorLogMessageParts) >= 7 Then mstrRemoteWorkstationName = Replace(strErrorLogMessageParts(7), "Workstation Name:", "")
                'On Error GoTo 0
                If UBound(strErrorLogMessageParts) >= 14 Then mstrRemoteIPAddress = Replace(strErrorLogMessageParts(14), "Source Network Address:", "")
                'On Error GoTo WriteErrorInfo
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
                strErrorSection = "EntryWritten:19"
                Select Case Trim(mstrLogonTypeID)
                    Case "0"
                        mstrLogonType = "SYSTEM"
                        mstrDescription = "Successful Network Logon by the SYSTEM Account (Event ID " & Trim(mstrEventID) & ")"
                    Case "2"
                        mstrLogonType = "Interactive"
                        mstrDescription = "Successful Network Logon to local console or with RUNAS (Event ID " & Trim(mstrEventID) & ")"
                    Case "3"
                        mstrLogonType = "Network"
                        mstrDescription = "Successful Network connection to a shared resource (Event ID " & Trim(mstrEventID) & ")"
                        If My.Settings.LogEvent540Type3 = False Then Exit Sub
                    Case "4"
                        mstrLogonType = "Batch"
                        mstrDescription = "Successful Network Logon by a Batch Process or Scheduled Task (Event ID " & Trim(mstrEventID) & ")"
                    Case "5"
                        mstrLogonType = "Service"
                        mstrDescription = "Successful Network Logon by a Service (Event ID " & Trim(mstrEventID) & ")"
                    Case "6"
                        mstrLogonType = "Proxy"
                        mstrDescription = "Successful Network Logon.  LogonType=6 (Proxy) (Event ID " & Trim(mstrEventID) & ")"
                    Case "7"
                        mstrLogonType = "Unlock"
                        mstrDescription = "Successful Network Workstation Unlock (Event ID " & Trim(mstrEventID) & ")"
                    Case "8"
                        mstrLogonType = "NetworkClearText"
                        mstrDescription = "Successful Network Logon via ASP script or IIS Basic Authentication (Event ID " & Trim(mstrEventID) & ")"
                    Case "9"
                        mstrLogonType = "NewCredentials"
                        mstrDescription = "Successful Network Logon with RUNAS netonly (Event ID " & Trim(mstrEventID) & ")"
                    Case "10"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Successful Network Logon via Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "11"
                        mstrLogonType = "CachedInteractive"
                        mstrDescription = "Successful Network Logon with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                    Case "12"
                        mstrLogonType = "RemoteInteractive"
                        mstrDescription = "Successful Network Logon via Terminal Services, Remote Desktop or Remote Assistance (Event ID " & Trim(mstrEventID) & ")"
                    Case "13"
                        mstrLogonType = "CachedUnlock"
                        mstrDescription = "Successful Network Workstation Unlock with cached credentials (Event ID " & Trim(mstrEventID) & ")"
                End Select
            Case "551", "4647" ' Logoff
                strErrorSection = "EntryWritten:20"
                mstrDescription = "User initiated logoff (Event ID " & Trim(mstrEventID) & ")"
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(2), "Domain:", "")
                mstrLogonID = Replace(strErrorLogMessageParts(3), "Logon ID:", "")
                mstrLocalLogoffDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "552", "4648"
                strErrorSection = "EntryWritten:21"
                If My.Settings.LogEvent552 = False Then Exit Sub
                mstrDescription = "Logon attempt using explicit credentials (Event ID " & Trim(mstrEventID) & ")"
                mstrLogonTypeID = ""
                mstrUsername = Replace(strErrorLogMessageParts(7), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(8), "Domain:", "")
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "627", "4723"
                strErrorSection = "EntryWritten:22"
                mstrDescription = "Logon failure - Account has an expired password, and user is attempting to change it during logon (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "644", "4740"
                strErrorSection = "EntryWritten:23"
                mstrDescription = "Logon failure - Account locked out (Event ID " & Trim(mstrEventID) & ")"
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "680"
                strErrorSection = "EntryWritten:24"
                mstrDescription = "Logon Attempt (Event ID " & Trim(mstrEventID) & ")"
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = strComputername
                mstrRemoteWorkstationName = Replace(strErrorLogMessageParts(2), "Source Workstation:", "")
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
            Case "682"
                strErrorSection = "EntryWritten:25"
                mstrDescription = "Session reconnected to Local Terminal Server (Event ID " & Trim(mstrEventID) & ")"
                mstrUsername = Replace(strErrorLogMessageParts(1), "User Name:", "")
                mstrDomainname = Replace(strErrorLogMessageParts(2), "Domain:", "")
                mstrLogonID = Replace(strErrorLogMessageParts(3), "Logon ID:", "")
                mstrRemoteSessionName = Replace(strErrorLogMessageParts(4), "Session Name:", "")
                mstrRemoteWorkstationName = Replace(strErrorLogMessageParts(5), "Client Name:", "")
                mstrRemoteIPAddress = Replace(strErrorLogMessageParts(6), "Client Address:", "")
                mstrLocalLogonDateTime = LogEntry.Entry.TimeGenerated
                bWriteLog = True
        End Select

        If Not bWriteLog Then Exit Sub

        If colExcludedUserList.Contains(mstrUsername.ToUpper) And mstrDescription.Contains("Successful") Then
            ' This user has been excluded from reporting if the event was a success
            Exit Sub
        End If

        strErrorSection = "EntryWritten:26"

        oUser.Username = mstrUsername
        oUser.Computername = strComputername
        oUser.Domainname = mstrDomainname
        oUser.EventDescription = mstrDescription
        oUser.ModelNumber = strModelNumber
        oUser.Manufacturer = strManufacturer
        oUser.SerialNumber = strSerialNumber
        oUser.Sitename = strSitename
        oUser.RemoteSessionName = mstrRemoteSessionName & " (" & mstrRemoteIPAddress & ")"
        oUser.RemoteClientName = mstrRemoteWorkstationName
        oUser.OperatingSystemName = strOperatingSystem

        oUser.GetIPAddresses()

        If lngTimezoneOffsetMinutes > -1 Then
            strOffsetSign = "+"
        Else
            strOffsetSign = "-"
        End If

        strErrorSection = "EntryWritten:27"

        If mstrLocalLogonDateTime.Trim <> "" Then
            oUser.UTCLogonDateTime = WMIFormattedDate(mstrLocalLogonDateTime, lngTimezoneOffsetMinutes)
        End If
        If mstrLocalLogoffDateTime.Trim <> "" Then
            oUser.UTCLogoffDateTime = WMIFormattedDate(mstrLocalLogoffDateTime, lngTimezoneOffsetMinutes)
        End If

        strErrorSection = "EntryWritten:28"
        ' =========================================================================
        ' Build RemoteSessionName
        If mstrRemoteSessionName <> "" And mstrRemoteIPAddress <> "" Then
            oUser.RemoteSessionName = mstrRemoteSessionName & " (" & mstrRemoteIPAddress & ")"
        End If
        If mstrRemoteSessionName = "" And mstrRemoteIPAddress <> "" Then
            oUser.RemoteSessionName = mstrRemoteIPAddress
        End If
        If mstrRemoteSessionName = "" And mstrRemoteIPAddress = "" Then
            oUser.RemoteSessionName = ""
        End If
        If mstrRemoteSessionName <> "" And mstrRemoteIPAddress = "" Then
            oUser.RemoteSessionName = mstrRemoteSessionName
        End If
        ' =========================================================================

        strErrorSection = "EntryWritten:29"
        oUser.Version = "1.6"

        strErrorSection = "EntryWritten:30"
        ' Only save the logfile if there is more than [MinimumDiskSpaceMbBeforeLogging] MB available on the C: drive
        If booOKToCreateLogFile Then
            strErrorSection = "EntryWritten:31"
            oUser.Serialize()
        Else
            strErrorLogMessage = "Unable to add Activity for User " & oUser.Username & " due to insufficient diskspace.  This information has been lost."
            WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Warning)
        End If

        Exit Sub
WriteErrorInfo:
        Dim strEventLogMessage As String = ""
        Dim i As Integer = 0

        strErrorLogMessage = "Error processing EventID " & mstrEventID & vbCrLf & _
                             "Error Description:" & vbCrLf & _
                             Err.Description & vbCrLf & _
                             "Event Details:" & vbCrLf & _
                             LogEntry.Entry.Message & vbCrLf & _
                             "Extracted information:" & vbCrLf & _
                             strErrorLogMessage & vbCrLf & _
                             "Message Parts:" & vbCrLf

        For i = 0 To UBound(strErrorLogMessageParts)
            strErrorLogMessage = strErrorLogMessage & CStr(i) & ": " & strErrorLogMessageParts(i) & vbCrLf
        Next

        strErrorLogMessage = strErrorLogMessage & _
                             "Error Section: " & strErrorSection & vbCrLf & _
                             "booOKToCreateLogFile: " & booOKToCreateLogFile

        WriteToEventLog(strErrorLogMessage, "Activity Log Client", EventLogEntryType.Error)
        Resume Next
    End Sub

    ' Turns a normal date into a string formatted as a WMI Date.  Requires a date and a timezone offset
    Private Function WMIFormattedDate(ByVal datDate As Date, ByVal lngTimezoneOffset As Long) As String
        Dim strDateTime As String = ""
        Dim strOffsetSign As String

        Try

            If lngTimezoneOffsetMinutes > -1 Then
                strOffsetSign = "+"
            Else
                strOffsetSign = "-"
            End If

            strDateTime = ZeroPadLeft(datDate.Year.ToString, 4) & _
                          ZeroPadLeft(datDate.Month.ToString, 2) & _
                          ZeroPadLeft(datDate.Day, 2) & _
                          ZeroPadLeft(datDate.Hour.ToString, 2) & _
                          ZeroPadLeft(datDate.Minute.ToString, 2) & _
                          ZeroPadLeft(datDate.Second.ToString, 2) & _
                          ".000000" & _
                          strOffsetSign & _
                          CStr(lngTimezoneOffset)
        Catch

        End Try

        Err.Clear()
        Return strDateTime
    End Function

    ' Ensures the given string is of the correct length, prefixing with zero's until it is
    Private Function ZeroPadLeft(ByVal strToBeFormatted As String, ByVal intFinalLength As Integer) As String

        Do Until Len(strToBeFormatted) >= intFinalLength
            strToBeFormatted = "0" & strToBeFormatted
        Loop

        Return strToBeFormatted

    End Function

End Class
