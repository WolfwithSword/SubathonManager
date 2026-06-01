using System.Windows;
using System.Windows.Controls;
using Microsoft.EntityFrameworkCore;
using SubathonManager.Core.Events;

namespace SubathonManager.UI;

public partial class MainWindow
{
    private void CapBtn_Click(object sender, RoutedEventArgs e)
    {
        if (CapPopup.IsOpen)
        {
            CapPopup.IsOpen = false;
            return;
        }

        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.AsNoTracking().FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        if (subathon.CapDateTime.HasValue)
        {
            var cap = subathon.CapDateTime.Value;
            CapDatePicker.SelectedDate = cap.Date;
            CapHourInput.Text = cap.Hour.ToString("D2");
            CapMinuteInput.Text = cap.Minute.ToString("D2");
            CapCurrentLabel.Text = $"Cap: {cap:MMM d, h:mm tt}";
            CapIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Flag24;
        }
        else
        {
            CapDatePicker.SelectedDate = DateTime.Today;
            CapHourInput.Text = DateTime.Now.AddHours(1).Hour.ToString("D2");
            CapMinuteInput.Text = "00";
            CapCurrentLabel.Text = "No cap set";
        }

        CapValidationMsg.Text = "";
        CapPopup.IsOpen = true;
    }

    private void CapDatePicker_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateCapInput();
    }

    private void CapTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateCapInput();
    }

    private bool ValidateCapInput()
    {
        if (CapDatePicker.SelectedDate == null)
        {
            CapValidationMsg.Text = "Please select a date.";
            SetCapBtn.IsEnabled = false;
            return false;
        }

        if (!int.TryParse(CapHourInput.Text, out int hour) || hour < 0 || hour > 23)
        {
            CapValidationMsg.Text = "Hour must be 0–23.";
            SetCapBtn.IsEnabled = false;
            return false;
        }

        if (!int.TryParse(CapMinuteInput.Text, out int minute) || minute < 0 || minute > 59)
        {
            CapValidationMsg.Text = "Minute must be 0–59.";
            SetCapBtn.IsEnabled = false;
            return false;
        }

        var picked = CapDatePicker.SelectedDate.Value.Date
            .AddHours(hour)
            .AddMinutes(minute);

        if (picked <= DateTime.Now)
        {
            CapValidationMsg.Text = "Cap must be in the future.";
            SetCapBtn.IsEnabled = false;
            return false;
        }

        CapValidationMsg.Text = "";
        SetCapBtn.IsEnabled = true;
        return true;
    }

    private void SetCap_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCapInput()) return;

        int hour = int.Parse(CapHourInput.Text);
        int minute = int.Parse(CapMinuteInput.Text);
        var capDateTime = CapDatePicker.SelectedDate!.Value.Date
            .AddHours(hour)
            .AddMinutes(minute);

        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        subathon.CapDateTime = capDateTime;
        db.SaveChanges();

        CapCurrentLabel.Text = $"Cap: {capDateTime:MMM d, h:mm tt}";
        CapIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Flag24;
        CapPopup.IsOpen = false;
        
        var snapshot = db.SubathonDatas
            .Where(x => x.Id == subathon.Id && x.IsActive)
            .Include(x => x.Multiplier)
            .AsNoTracking()
            .FirstOrDefault();
        if (snapshot != null)
            SubathonEvents.RaiseSubathonDataUpdate(snapshot, DateTime.Now);
    }

    private void ClearCap_Click(object sender, RoutedEventArgs e)
    {
        using var db = _factory.CreateDbContext();
        var subathon = db.SubathonDatas.FirstOrDefault(s => s.IsActive);
        if (subathon == null) return;

        subathon.CapDateTime = null;
        db.SaveChanges();

        CapCurrentLabel.Text = "No cap set";
        CapIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.FlagOff24;
        CapPopup.IsOpen = false;
    }
}