using System;
using System.ComponentModel.DataAnnotations;
namespace TrainTracking.Domain.Entities;
public class Train
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TrainNumber { get; set; } = null!;
    public string Type { get; set; } = null!;

    [Required(ErrorMessage = "عدد المقاعد مطلوب")]
    [Range(20, 1000, ErrorMessage = "عدد المقاعد يجب أن يكون بين 20 و 1000")]
    public int TotalSeats { get; set; }
}
