using SmartPlanif.Infrastructure.MySql;
using Xunit;

namespace SmartPlanif.Domain.Tests;

/// <summary>
/// Lecture des données réelles : durée d'un voyage déduite de l'itinéraire (dritin) quand stop_times est vide, et règle d'activité
/// des bus quand DEACTIF n'est pas renseigné. Ces cas viennent du diagnostic d'une vraie base.
/// </summary>
public class SourceOptionsTests
{
    private static ItineraryStop[] Stops(long[] minutes, long? maxKm) =>
        minutes.Select((m, i) => new ItineraryStop(i + 1, m, i == minutes.Length - 1 ? maxKm : i)).ToArray();

    [Fact]
    public void Cumulative_minutes_give_the_last_value_as_trip_duration()
    {
        var result = ItineraryDuration.Resolve(Stops(new long[] { 0, 15, 30, 45, 60, 75 }, 22), ItineraryDurationMode.Auto);
        Assert.Equal(TimeSpan.FromMinutes(75), result.Duration);
    }

    [Fact]
    public void Leg_durations_are_summed_when_they_are_not_increasing()
    {
        var result = ItineraryDuration.Resolve(Stops(new long[] { 0, 15, 15, 15, 15, 15 }, 22), ItineraryDurationMode.Auto);
        Assert.Equal(TimeSpan.FromMinutes(75), result.Duration);
    }

    [Fact]
    public void An_ambiguous_increasing_series_is_settled_by_the_average_speed()
    {
        // 0,10,20,30 : cumulé = 30 min pour 40 km (80 km/h), somme = 60 min (40 km/h, plus vraisemblable pour un bus).
        var result = ItineraryDuration.Resolve(Stops(new long[] { 0, 10, 20, 30 }, 40), ItineraryDurationMode.Auto);
        Assert.Equal(TimeSpan.FromMinutes(60), result.Duration);
    }

    [Fact]
    public void Explicit_modes_override_the_detection()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), ItineraryDuration.Resolve(Stops(new long[] { 0, 15, 30 }, 20), ItineraryDurationMode.MaxCumulative).Duration);
        Assert.Equal(TimeSpan.FromMinutes(45), ItineraryDuration.Resolve(Stops(new long[] { 0, 15, 30 }, 20), ItineraryDurationMode.SumOfLegs).Duration);
    }

    [Fact]
    public void An_empty_or_zero_itinerary_gives_no_duration()
    {
        Assert.True(ItineraryDuration.Resolve(Array.Empty<ItineraryStop>(), ItineraryDurationMode.Auto).Duration is null);
        Assert.True(ItineraryDuration.Resolve(new[] { new ItineraryStop(1, null, null), new ItineraryStop(2, null, null) }, ItineraryDurationMode.Auto).Duration is null);
        Assert.True(ItineraryDuration.Resolve(Stops(new long[] { 0, 0, 0 }, 10), ItineraryDurationMode.Auto).Duration is null);
    }

    [Fact]
    public void Buses_are_all_active_when_the_activity_flag_is_not_filled_in()
    {
        // Cas réel : DEACTIF = 0 pour tous les bus -> en mode Auto, tous sont actifs.
        Assert.True(MySqlResourceRepository.IsBusActive(BusActiveMode.Auto, anyBusFlaggedActive: false, flag: 0));
    }

    [Fact]
    public void The_activity_flag_is_used_as_soon_as_it_is_filled_in_for_some_buses()
    {
        Assert.True(MySqlResourceRepository.IsBusActive(BusActiveMode.Auto, anyBusFlaggedActive: true, flag: 1));
        Assert.False(MySqlResourceRepository.IsBusActive(BusActiveMode.Auto, anyBusFlaggedActive: true, flag: 0));
    }

    [Fact]
    public void Strict_and_permissive_bus_modes_ignore_detection()
    {
        Assert.False(MySqlResourceRepository.IsBusActive(BusActiveMode.FlagIsOne, anyBusFlaggedActive: false, flag: 0));
        Assert.True(MySqlResourceRepository.IsBusActive(BusActiveMode.AllActive, anyBusFlaggedActive: true, flag: 0));
    }
}
