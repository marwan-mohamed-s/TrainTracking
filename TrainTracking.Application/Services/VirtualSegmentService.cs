using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using TrainTracking.Domain.Entities;
using TrainTracking.Application.Interfaces;

namespace TrainTracking.Application.Services
{
    public class VirtualSegmentService : IVirtualSegmentService
    {
        private readonly IStationRepository _stationRepository;

        public VirtualSegmentService(IStationRepository stationRepository)
        {
            _stationRepository = stationRepository;
        }

        private async Task<double> GetTotalPathDistanceAsync(Trip trip)
        {
            var stations = (await _stationRepository.GetAllAsync())
                .Where(s => s.Order >= trip.FromStation!.Order && s.Order <= trip.ToStation!.Order)
                .OrderBy(s => s.Order)
                .ToList();

            double totalPathDistance = 0;
            for (int i = 0; i < stations.Count - 1; i++)
            {
                totalPathDistance += CalculateDistance(
                    stations[i].Latitude, stations[i].Longitude,
                    stations[i + 1].Latitude, stations[i + 1].Longitude);
            }
            return totalPathDistance;
        }

        private async Task<double> GetSegmentPathDistanceAsync(Station from, Station to)
        {
            var stations = (await _stationRepository.GetAllAsync())
                .Where(s => s.Order >= Math.Min(from.Order, to.Order) && s.Order <= Math.Max(from.Order, to.Order))
                .OrderBy(s => s.Order)
                .ToList();

            double segmentDistance = 0;
            for (int i = 0; i < stations.Count - 1; i++)
            {
                segmentDistance += CalculateDistance(
                    stations[i].Latitude, stations[i].Longitude,
                    stations[i + 1].Latitude, stations[i + 1].Longitude);
            }
            return segmentDistance;
        }

        public async Task<decimal> CalculatePriceAsync(Trip trip, Station fromStation, Station toStation)
        {
            if (trip == null || fromStation == null || toStation == null) return 0;

            double segmentDistance = await GetSegmentPathDistanceAsync(fromStation, toStation);
            double totalTripDistance = await GetTotalPathDistanceAsync(trip);

            if (totalTripDistance <= 0) return trip.Price;

            double ratio = segmentDistance / totalTripDistance;
            decimal calculatedPrice = trip.Price * (decimal)ratio;

            return Math.Round(calculatedPrice, 3);
        }

        public async Task<DateTimeOffset> CalculateDepartureTimeAsync(Trip trip, Station fromStation)
        {
            if (trip.FromStationId == fromStation.Id)
                return trip.DepartureTime;

            if (trip.ToStationId == fromStation.Id)
                return trip.ArrivalTime;

            double totalDistance = await GetTotalPathDistanceAsync(trip);
            double totalDurationMinutes = (trip.ArrivalTime - trip.DepartureTime).TotalMinutes;

            if (totalDistance <= 0 || totalDurationMinutes <= 0) return trip.DepartureTime;

            double distanceFromStart = await GetSegmentPathDistanceAsync(trip.FromStation!, fromStation);

            double timeOffsetMinutes = (distanceFromStart / totalDistance) * totalDurationMinutes;

            return trip.DepartureTime.AddMinutes(timeOffsetMinutes);
        }

        public async Task<DateTimeOffset> CalculateArrivalTimeAsync(Trip trip, Station toStation)
        {
            if (trip.ToStationId == toStation.Id)
                return trip.ArrivalTime;

            if (trip.FromStationId == toStation.Id)
                return trip.DepartureTime;

            double totalDistance = await GetTotalPathDistanceAsync(trip);
            double totalDurationMinutes = (trip.ArrivalTime - trip.DepartureTime).TotalMinutes;

            if (totalDistance <= 0 || totalDurationMinutes <= 0) return trip.ArrivalTime;

            double distanceFromStart = await GetSegmentPathDistanceAsync(trip.FromStation!, toStation);

            double timeOffsetMinutes = (distanceFromStart / totalDistance) * totalDurationMinutes;

            return trip.DepartureTime.AddMinutes(timeOffsetMinutes);
        }

        private double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
        {
            var r = 6371; // Radius of the earth in km
            var dLat = DegreeToRadian(lat2 - lat1);
            var dLon = DegreeToRadian(lon2 - lon1);
            var a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreeToRadian(lat1)) * Math.Cos(DegreeToRadian(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return r * c;
        }

        private double DegreeToRadian(double deg)
        {
            return deg * (Math.PI / 180);
        }
    }
}
