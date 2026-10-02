using System.Text;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Studue.Services;

public static class IcalService
{
    // All schedule times and assignment due dates are Zurich wall-clock time
    // (see Helper.Now()), so the feed must say so instead of emitting floating time.
    private const string TimeZoneId = "Europe/Zurich";

    public static void RegisterEndpoint(WebApplication webApplication)
    {
        webApplication.MapGet("/ical/{studentId}", Get);
    }

    private static async Task<IResult> Get(
        string studentId,
        DatabaseContext databaseContext,
        SemesterService semesterService
    )
    {
        studentId = studentId.Trim().ToLowerInvariant();

        var student = await databaseContext.Students
            .Where(x => x.StudentId == studentId)
            .Include(x => x.ModuleInstances)
            .ThenInclude(x => x.Module)
            .Include(x => x.ModuleInstances)
            .ThenInclude(x => x.ScheduleEntries)
            .Include(x => x.ModuleInstances)
            .ThenInclude(x => x.Assignements)
            .FirstOrDefaultAsync();

        if (student == null)
            return Results.NotFound();

        var calendar = new Calendar
        {
            Method = CalendarMethods.Publish,
            ProductId = "-//Studue//Class Schedule//DE",
        };
        calendar.AddTimeZone(new VTimeZone(TimeZoneId));

        foreach (var moduleInstance in student.ModuleInstances)
        {
            // Classes need semester weeks to be placed on real dates, assignments don't.
            // A missing week list must only skip the classes, never the assignments.
            var weeks = await semesterService.GetCachedWeeks(moduleInstance.Semester);
            if (weeks is { Count: > 0 })
            {
                foreach (var scheduleEntry in moduleInstance.ScheduleEntries)
                {
                    var endTime = ScheduleSlots.EndTimeOf(
                        scheduleEntry.StartTime,
                        scheduleEntry.Duration
                    );

                    // One VEVENT per actual teaching week: the week list has gaps for
                    // holidays, which a weekly RRULE from semester start to end would
                    // incorrectly fill with classes.
                    foreach (var week in weeks)
                    {
                        var date = DateForWeekday(week, scheduleEntry.Weekday);
                        if (date is null)
                            continue;

                        var start = date.Value.ToDateTime(scheduleEntry.StartTime);
                        var end = date.Value.ToDateTime(endTime);

                        calendar.Events.Add(
                            new CalendarEvent
                            {
                                Uid =
                                    $"class-{moduleInstance.Semester}-{scheduleEntry.Id}-{date.Value:yyyyMMdd}@studue.ch",
                                Summary = moduleInstance.Module.Name,
                                Description = $"{scheduleEntry.Teacher}\n{scheduleEntry.Room}",
                                Location = scheduleEntry.Room,
                                Start = new CalDateTime(start, TimeZoneId),
                                End = new CalDateTime(end, TimeZoneId),
                            }
                        );
                    }
                }
            }

            foreach (
                var assignment in moduleInstance.Assignements.Where(x => !x.IsDeleted)
            )
            {
                calendar.Events.Add(
                    new CalendarEvent
                    {
                        Uid = $"assignment-{assignment.Id}@studue.ch",
                        Summary = $"Assignment: {assignment.Title}",
                        Description = assignment.Description,
                        Start = new CalDateTime(assignment.DueDateTime, TimeZoneId),
                        End = new CalDateTime(
                            assignment.DueDateTime.AddMinutes(30),
                            TimeZoneId
                        ),
                    }
                );
            }
        }

        var serialized =
            new CalendarSerializer().SerializeToString(calendar)
            ?? throw new InvalidOperationException("Failed to serialize the iCal calendar.");
        return Results.File(
            Encoding.UTF8.GetBytes(serialized),
            "text/calendar; charset=utf-8",
            $"{student.StudentId}.ics"
        );
    }

    // Weekday follows the schedule grid: 0 = Monday .. 5 = Saturday
    // (see ScheduleComponent._weekdays and StudentContext week parsing).
    // Semester weeks come from stundenplan.zhaw.ch as date ranges and are not
    // guaranteed to start on a Monday, so derive the offset from the week's
    // actual start instead of assuming semesterStart.AddDays(weekday).
    private static DateOnly? DateForWeekday(SemesterWeek week, int weekday)
    {
        var weekStartMondayBased = ((int)week.Start.DayOfWeek + 6) % 7;
        var date = week.Start.AddDays(weekday - weekStartMondayBased);
        if (date < week.Start || date > week.End)
            return null;
        return date;
    }
}
