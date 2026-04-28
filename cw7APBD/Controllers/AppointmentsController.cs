using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using cw7APBD.DTOs; 
using System.Data;

namespace cw7APBD.Controllers;

[Route("api/appointments")]
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly IConfiguration _configuration;

    public AppointmentsController(IConfiguration configuration)
    {
        _configuration = configuration;
    }
    
    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        var appointments = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(connectionString);
        string sql = @"
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, 
                   p.FirstName + ' ' + p.LastName, p.Email
            FROM Appointments a
            JOIN Patients p ON p.IdPatient = a.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@PatientLastName", (object?)patientLastName ?? DBNull.Value);

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }
        return Ok(appointments);
    }
    
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAppointment(int id)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new SqlConnection(connectionString);
        
        string sql = @"
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes,
                   p.FirstName + ' ' + p.LastName, p.Email,
                   d.FirstName + ' ' + d.LastName, s.Name
            FROM Appointments a
            JOIN Patients p ON a.IdPatient = p.IdPatient
            JOIN Doctors d ON a.IdDoctor = d.IdDoctor
            JOIN Specializations s ON d.IdSpecialization = s.IdSpecialization
            WHERE a.IdAppointment = @Id";

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", id);

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (await reader.ReadAsync())
        {
            return Ok(new AppointmentDetailsDto {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
                PatientFullName = reader.GetString(5),
                PatientEmail = reader.GetString(6),
                DoctorFullName = reader.GetString(7),
                SpecializationName = reader.GetString(8)
            });
        }
        return NotFound();
    }
    
    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto request)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        
        string checkSql = "SELECT COUNT(*) FROM Appointments WHERE IdDoctor = @IdD AND AppointmentDate = @Date";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        checkCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        
        if ((int)await checkCmd.ExecuteScalarAsync() > 0) 
            return Conflict("Doctor is busy at this time.");
        
        string insertSql = @"
            INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason)
            VALUES (@IdP, @IdD, @Date, 'Scheduled', @Reason);
            SELECT SCOPE_IDENTITY();";
        
        await using var insertCmd = new SqlCommand(insertSql, connection);
        insertCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        insertCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        insertCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        insertCmd.Parameters.AddWithValue("@Reason", request.Reason);

        var newId = await insertCmd.ExecuteScalarAsync();
        return CreatedAtAction(nameof(GetAppointment), new { id = Convert.ToInt32(newId) }, null);
    }
}