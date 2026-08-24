Imports System.Data
Imports System.Data.SqlClient

Partial Class Results
    Inherits System.Web.UI.Page

    Protected Sub Page_Load(ByVal sender As Object, ByVal e As System.EventArgs) Handles Me.Load
        Me.SqlDataSource1.SelectCommand = "SELECT * FROM tblLogonEvents WHERE ID = " & Request.QueryString("EventID")

        'log this action
        Dim UserAccount As String = Request.ServerVariables("LOGON_USER")
        Dim myConnection As New SqlConnection(ConfigurationManager.ConnectionStrings("LALConnectionString").ConnectionString)
        Dim myCommand As New SqlCommand("INSERT INTO tblWebAccessAudit (Description, AuditDate, AuditBy) VALUES ('View record details: [EventID=" & Request.QueryString("EventID") & "]', GETDATE(), '" & UserAccount & "')", myConnection)
        myCommand.Connection.Open()
        myCommand.ExecuteNonQuery()
        myCommand.Connection.Close()
    End Sub
End Class
