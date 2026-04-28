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
    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment(int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        
        string getExistingSql = "SELECT Status, AppointmentDate FROM Appointments WHERE IdAppointment = @Id";
        await using var getCmd = new SqlCommand(getExistingSql, connection);
        getCmd.Parameters.AddWithValue("@Id", idAppointment);
        await using var reader = await getCmd.ExecuteReaderAsync();
        
        if (!await reader.ReadAsync()) return NotFound("Appointment not found.");

        string currentStatus = reader.GetString(0);
        DateTime currentDate = reader.GetDateTime(1);
        await reader.CloseAsync();

        
        var allowedStatuses = new[] { "Scheduled", "Completed", "Cancelled" };
        if (!allowedStatuses.Contains(request.Status))
            return BadRequest("Invalid status.");
        
        if (currentStatus == "Completed" && request.AppointmentDate != currentDate)
            return BadRequest("Cannot change date for a completed appointment.");

        
        string checkSql = @"
            SELECT (SELECT COUNT(*) FROM Patients WHERE IdPatient = @IdP AND IsActive = 1),
                   (SELECT COUNT(*) FROM Doctors WHERE IdDoctor = @IdD AND IsActive = 1)";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        checkCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        await using var checkReader = await checkCmd.ExecuteReaderAsync();
        await checkReader.ReadAsync();
        if (checkReader.GetInt32(0) == 0 || checkReader.GetInt32(1) == 0)
            return BadRequest("Patient or Doctor not found/inactive.");
        await checkReader.CloseAsync();

        
        if (request.AppointmentDate != currentDate)
        {
            string conflictSql = "SELECT COUNT(*) FROM Appointments WHERE IdDoctor = @IdD AND AppointmentDate = @Date AND IdAppointment != @Id";
            await using var conflictCmd = new SqlCommand(conflictSql, connection);
            conflictCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
            conflictCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
            conflictCmd.Parameters.AddWithValue("@Id", idAppointment);
            if ((int)await conflictCmd.ExecuteScalarAsync() > 0)
                return Conflict("Doctor is busy at this time.");
        }

        
        string updateSql = @"
            UPDATE Appointments 
            SET IdPatient = @IdP, IdDoctor = @IdD, AppointmentDate = @Date, 
                Status = @Status, Reason = @Reason, InternalNotes = @Notes
            WHERE IdAppointment = @Id";
        
        await using var updateCmd = new SqlCommand(updateSql, connection);
        updateCmd.Parameters.AddWithValue("@IdP", request.IdPatient);
        updateCmd.Parameters.AddWithValue("@IdD", request.IdDoctor);
        updateCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        updateCmd.Parameters.AddWithValue("@Status", request.Status);
        updateCmd.Parameters.AddWithValue("@Reason", request.Reason);
        updateCmd.Parameters.AddWithValue("@Notes", (object?)request.InternalNotes ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("@Id", idAppointment);

        await updateCmd.ExecuteNonQueryAsync();
        return NoContent();
    }
    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment(int idAppointment)
    {
        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        
        string checkSql = "SELECT Status FROM Appointments WHERE IdAppointment = @Id";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@Id", idAppointment);
    
        var result = await checkCmd.ExecuteScalarAsync();

        if (result == null) 
            return NotFound($"Appointment with ID {idAppointment} not found.");

        string status = result.ToString()!;

        if (status == "Completed")
            return Conflict("Cannot delete an appointment that has already been completed.");

        string deleteSql = "DELETE FROM Appointments WHERE IdAppointment = @Id";
        await using var deleteCmd = new SqlCommand(deleteSql, connection);
        deleteCmd.Parameters.AddWithValue("@Id", idAppointment);
    
        await deleteCmd.ExecuteNonQueryAsync();
        
        return NoContent();
    }
}

