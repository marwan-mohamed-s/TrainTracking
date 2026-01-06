using Microsoft.AspNetCore.Mvc;
using TrainTracking.Application.Interfaces;
using TrainTracking.Domain.Entities;
using TrainTracking.Domain.Enums;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace TrainTracking.Web.Controllers
{
    public class BookingsController : Controller
    {
        private readonly IBookingRepository _bookingRepository;
        private readonly ITripRepository _tripRepository;
        private readonly Services.TicketGenerator _ticketGenerator;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly INotificationRepository _notificationRepository;
        private readonly IDateTimeService _dateTimeService;

        public BookingsController(IBookingRepository bookingRepository, ITripRepository tripRepository, 
            Services.TicketGenerator ticketGenerator, IEmailService emailService, ISmsService smsService,
            INotificationRepository notificationRepository, IDateTimeService dateTimeService)
        {
            _bookingRepository = bookingRepository;
            _tripRepository = tripRepository;
            _ticketGenerator = ticketGenerator;
            _emailService = emailService;
            _smsService = smsService;
            _notificationRepository = notificationRepository;
            _dateTimeService = dateTimeService;
        }

        [HttpGet]
        public async Task<IActionResult> Create(Guid? id, Guid? tripId)
        {
            var targetId = id ?? tripId;
            if (targetId == null || targetId == Guid.Empty)
            {
                return BadRequest("Trip ID is required.");
            }

            var trip = await _tripRepository.GetTripWithStationsAsync(targetId.Value);
            if (trip == null)
            {
                return NotFound("لم يتم العثور على الرحلة المطلوبة.");
            }

            ViewBag.TakenSeats = await _bookingRepository.GetTakenSeatsAsync(targetId.Value);

            var booking = new Booking
            {
                TripId = targetId.Value,
                Trip = trip,
                Price = 2 
            };

            return View(booking);
        }



        /// ///////////////////////////////////////////////////////////////////////////////<summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Booking booking, string selectedSeats)
        {
             ModelState.Remove("Trip");
             ModelState.Remove("UserId");

            if (ModelState.IsValid)
            {
                if (await _bookingRepository.IsSeatTakenAsync(booking.TripId, booking.SeatNumber))
                { 
                    ModelState.AddModelError("SeatNumber", "هذا المقعد محجوز بالفعل.");
                }
                else if (string.IsNullOrEmpty(selectedSeats))
                {
                    ModelState.AddModelError("", "يجب اختيار مقعد واحد على الأقل.");
                    return View(booking);
                }
                else
                {
                    var seatNumbers = selectedSeats.Split(',').Select(int.Parse).ToList();
                    var createdBookingIds = new List<Guid>();

                    foreach (var seat in seatNumbers)
                    {
                        var newBooking = new Booking
                        {
                            Id = Guid.NewGuid(),
                            TripId = booking.TripId,
                            PassengerName = booking.PassengerName,
                            PassengerPhone = booking.PassengerPhone,
                            SeatNumber = seat, // هنا نضع رقم المقعد من اللوب
                            Price = booking.Price, // سعر المقعد الواحد
                            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "Guest",
                            Status = BookingStatus.PendingPayment,
                            BookingDate = DateTimeOffset.Now
                        };

                        // حفظ كل مقعد كسجل منفصل في قاعدة البيانات
                        await _bookingRepository.CreateAsync(newBooking);
                        createdBookingIds.Add(newBooking.Id);
                    }

                    // Redirect to Payment
                    string idsString = string.Join(",", createdBookingIds);
                    return RedirectToAction("Payment", new { ids = idsString });
                }
            }

            var trip = await _tripRepository.GetTripWithStationsAsync(booking.TripId);
            if (trip != null)
            {
                booking.Trip = trip;
            }
            ViewBag.TakenSeats = await _bookingRepository.GetTakenSeatsAsync(booking.TripId);
            return View(booking);
        }


        /// //////////////////////////////////////////////////////////////////<summary>
        [HttpGet]
        [Authorize]
        public async Task<IActionResult> Payment(string ids) // استلام المعرفات كنص (مثل: id1,id2,id3)
        {
            if (string.IsNullOrEmpty(ids)) return RedirectToAction("Index", "Home");

            // 1. تحويل النص إلى قائمة من الـ Guids
            var bookingIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(Guid.Parse)
                                .ToList();

            var bookings = new List<Booking>();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // 2. جلب كل الحجوزات من قاعدة البيانات
            foreach (var id in bookingIds)
            {
                var booking = await _bookingRepository.GetByIdAsync(id);

                // التحقق من صحة الحجز وملكيته للمستخدم
                if (booking != null && booking.UserId == userId && booking.Status == BookingStatus.PendingPayment)
                {
                    bookings.Add(booking);
                }
            }

            // 3. إذا لم نجد أي حجز صالح
            if (!bookings.Any()) return NotFound("لا توجد حجوزات صالحة للدفع.");

            // 4. إرسال قائمة الحجوزات إلى الـ View
            // ملاحظة: الموديل في صفحة Payment.cshtml يجب أن يكون الآن IEnumerable<Booking>
            return View(bookings);
        }




        /// <summary>
        /// //////////////////////////////////////////////////////////////////////////////////////////////////////////
        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcessPayment(string ids, string? bank, string? cardNumber, string? expiryDate, string? pin, string paymentMethod = "KNET")
        {
            if (string.IsNullOrEmpty(ids)) return RedirectToAction("Index", "Home");

            // 1. تحويل النص "id1,id2,id3" إلى قائمة Guids
            var bookingIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(Guid.Parse)
                                .ToList();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var confirmedBookings = new List<Booking>();

            // 2. التحقق من صحة الحجوزات (أمنياً وبرمجياً)
            foreach (var id in bookingIds)
            {
                var booking = await _bookingRepository.GetByIdAsync(id);
                if (booking != null && booking.UserId == userId && booking.Status == BookingStatus.PendingPayment)
                {
                    confirmedBookings.Add(booking);
                }
            }

            if (!confirmedBookings.Any()) return NotFound("لا توجد حجوزات صالحة للمعالجة.");

            // 3. معالجة الدفع (Mock Payment)
            await Task.Delay(1500);

            if (paymentMethod == "KNET" && string.IsNullOrEmpty(pin))
            {
                ModelState.AddModelError("pin", "يرجى إدخال الرقم السري");
                // نرجع لصفحة الدفع مع قائمة الحجوزات لإظهار الخطأ
                return View("Payment", confirmedBookings);
            }

            // 4. تحديث حالة كل الحجوزات إلى "Confirmed"
            foreach (var booking in confirmedBookings)
            {
                booking.Status = BookingStatus.Confirmed;
                await _bookingRepository.UpdateAsync(booking);
            }

            // 5. إرسال الإشعارات (نرسل رسالة واحدة تلخص العملية)
            var firstBooking = confirmedBookings.First();
            var seatNumbers = string.Join(", ", confirmedBookings.Select(b => b.SeatNumber));
            var totalPrice = confirmedBookings.Sum(b => b.Price);

            // البريد الإلكتروني
            await _emailService.SendEmailAsync("user@example.com", "تأكيد حجز مقاعد القطار",
                $"عزيزي {firstBooking.PassengerName}، تم تأكيد حجزك للمقاعد ({seatNumbers}) بنجاح. الإجمالي المدفوع: {totalPrice} KD.");

            // رسالة SMS
            var phoneNumber = firstBooking.PassengerPhone;
            if (!phoneNumber.StartsWith("+") && phoneNumber.Length == 8) phoneNumber = "+965" + phoneNumber;

            var smsMessage = $"✅ تم دفع {totalPrice} KD بنجاح! مقاعدك: ({seatNumbers}) مؤكدة الآن. رحلة سعيدة! 🚂💳";
            var smsResult = await _smsService.SendSmsAsync(phoneNumber, smsMessage);

            // حفظ سجل الإشعار لأول حجز كمرجع
            await _notificationRepository.CreateAsync(new Notification
            {
                Recipient = phoneNumber,
                Message = smsMessage,
                Type = NotificationType.SMS,
                BookingId = firstBooking.Id,
                TripId = firstBooking.TripId,
                IsSent = smsResult.Success,
                ErrorMessage = smsResult.ErrorMessage
            });

            // التوجيه لصفحة النجاح (نرسل أول ID فقط لغرض العرض)
            return RedirectToAction(nameof(Success), new { ids = ids });
        }

        /// <summary>
        /// ////////////////////////////////////////////////////////////////////////لسه هغير فيها
        /// </summary>
        /// <returns></returns>
        [Authorize]
        public async Task<IActionResult> MyBookings()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Forbid();

            var bookings = await _bookingRepository.GetBookingsByUserIdAsync(userId);
            
            // Real Points = (Confirmed Bookings * 10) - Redeemed Points
            var earnedPoints = (int)bookings
                .Where(b => b.Status == BookingStatus.Confirmed)
                .Sum(b => b.Price * 10);
            
            var redeemedPoints = await _bookingRepository.GetRedeemedPointsAsync(userId);
            ViewBag.TotalPoints = earnedPoints - redeemedPoints;

            return View(bookings);
        }

        [Authorize]
        public async Task<IActionResult> Rewards()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Forbid();

            var bookings = await _bookingRepository.GetBookingsByUserIdAsync(userId);
            var confirmedBookings = bookings.Where(b => b.Status == BookingStatus.Confirmed).ToList();
            
            var earnedPoints = (int)confirmedBookings.Sum(b => b.Price * 10);
            var redeemedPoints = await _bookingRepository.GetRedeemedPointsAsync(userId);
            
            ViewBag.TotalPoints = earnedPoints - redeemedPoints;
            ViewBag.ConfirmedBookings = confirmedBookings;

            return View();
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> RedeemPoints()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null) return Forbid();

            var bookings = await _bookingRepository.GetBookingsByUserIdAsync(userId);
            var earnedPoints = (int)bookings
                .Where(b => b.Status == BookingStatus.Confirmed)
                .Sum(b => b.Price * 10);
            
            var redeemedPointsBefore = await _bookingRepository.GetRedeemedPointsAsync(userId);
            var currentPoints = earnedPoints - redeemedPointsBefore;

            if (currentPoints < 200)
            {
                TempData["Error"] = "عذراً، تحتاج إلى 200 نقطة على الأقل للحصول على تذكرة مجانية.";
                return RedirectToAction(nameof(Rewards));
            }

            // Persistence: Deduct points by creating a redemption record
            var redemption = new PointRedemption
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PointsRedeemed = 200,
                RedemptionDate = _dateTimeService.Now,
                Description = "استبدال تذكرة مجانية (200 نقطة)"
            };

            await _bookingRepository.CreateRedemptionAsync(redemption);

            TempData["Success"] = "تهانينا! لقد قمت بتحويل 200 نقطة إلى تذكرة مجانية بنجاح. تم خصم النقاط من رصيدك.";
            
            return RedirectToAction(nameof(Rewards));
        }
        /////////////////////////////////////////////////////////////////////////////////////////////////////////
        [HttpGet]
        public IActionResult Success(string ids) // غيرنا النوع من Guid لـ string ليقبل "id1,id2,id3"
        {
            if (string.IsNullOrEmpty(ids))
            {
                // إذا لم توجد معرفات، ارجع للرئيسية أو صفحة حجوزاتي
                return RedirectToAction("MyBookings");
            }

            // تأكد من أن الاسم "BookingIds" بالجمع تماماً كما كتبته في الـ View
            ViewBag.BookingIds = ids;

            return View();
        }

        [HttpGet("Bookings/DownloadTickets")] // تم تغيير الاسم للجمع وتغيير مسار الـ Route
        public async Task<IActionResult> DownloadTickets(string ids)
        {
            if (string.IsNullOrEmpty(ids)) return BadRequest("No ticket IDs provided.");

            // 1. تحويل المعرفات من نص إلى قائمة
            var bookingIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(Guid.Parse)
                                .ToList();

            var bookings = new List<Booking>();
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            bool isAuthenticated = User.Identity?.IsAuthenticated ?? false;
            bool isAdmin = isAuthenticated && User.IsInRole("Admin");

            // 2. جلب الحجوزات والتحقق من الصلاحيات لكل حجز
            foreach (var id in bookingIds)
            {
                var booking = await _bookingRepository.GetByIdAsync(id);
                if (booking == null) continue;

                bool isOwner = (isAuthenticated && booking.UserId == userId);
                bool isAnonymousBooking = string.IsNullOrEmpty(booking.UserId) || booking.UserId == "Anonymous";

                if (isOwner || isAnonymousBooking || isAdmin)
                {
                    bookings.Add(booking);
                }
            }

            if (!bookings.Any()) return Forbid();

            // 3. تجهيز الروابط (QR Codes) لكل تذكرة
            var request = HttpContext.Request;
            var host = request.Host.Value;
            var scheme = request.Scheme;

            if (host.Contains("localhost") || host.Contains("127.0.0.1"))
            {
                try
                {
                    var localIp = GetLocalIpAddress();
                    if (!string.IsNullOrEmpty(localIp))
                    {
                        host = $"{localIp}:5244";
                        scheme = "http";
                    }
                }
                catch { /* Fallback safe */ }
            }

            var baseUrl = $"{scheme}://{host}";

            // إنشاء قائمة تحتوي على بيانات الحجز مع رابط الـ QR الخاص به
            var ticketsData = bookings.Select(b => new {
                Booking = b,
                QrUrl = $"{baseUrl}/Bookings/TicketDetails/{b.Id}"
            }).ToList();

            // 4. استدعاء المولد لإنشاء ملف PDF واحد يحتوي على كل التذاكر
            // ملاحظة: يجب تعديل ميثود GenerateTicketPdf في الـ Service لتقبل قائمة أو عمل ميثود جديدة
            var pdf = _ticketGenerator.GenerateMultipleTicketsPdf(bookings, baseUrl);

            // إرجاع الملف باسم معبر
            string fileName = bookings.Count > 1 ? $"Tickets-Group-{DateTime.Now:yyyyMMdd}.pdf" : $"Ticket-{bookings[0].Id.ToString()[..8]}.pdf";
            return File(pdf, "application/pdf", fileName);
        }

        private string? GetLocalIpAddress()
        {
            var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            
            // First check for 192.168.x.x (Most common home network)
            var homeIp = host.AddressList.FirstOrDefault(ip => 
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && 
                !System.Net.IPAddress.IsLoopback(ip) && 
                ip.ToString().StartsWith("192.168."));

            if (homeIp != null) return homeIp.ToString();

            // Then check for 10.x.x.x or 172.x.x.x (Enterprise/Other)
            var otherIp = host.AddressList.FirstOrDefault(ip => 
                ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && 
                !System.Net.IPAddress.IsLoopback(ip));

            return otherIp?.ToString();
        }

        [AllowAnonymous]
        public async Task<IActionResult> TicketDetails(Guid id)
        {
            var booking = await _bookingRepository.GetByIdAsync(id);
            if (booking == null) return NotFound();

            return View(booking);
        }

        [Authorize]
        public async Task<IActionResult> Cancel(Guid id)
        {
            var booking = await _bookingRepository.GetByIdAsync(id);
            if (booking == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (booking.UserId != userId) return Forbid();

            if (booking.Status == BookingStatus.Cancelled)
            {
                return BadRequest("هذا الحجز ملغي بالفعل.");
            }

            var now = _dateTimeService.Now;
            var timeToDeparture = booking.Trip.DepartureTime - now;
            if (timeToDeparture.TotalSeconds <= 0)
            {
                return BadRequest("لا يمكن إلغاء حجز لرحلة قد بدأت بالفعل.");
            }

            decimal deductionPercentage = timeToDeparture.TotalHours <= 24 ? 25 : 10;
            decimal refundAmount = booking.Price * (1 - deductionPercentage / 100);

            ViewBag.DeductionPercentage = deductionPercentage;
            ViewBag.RefundAmount = refundAmount;

            return View(booking);
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelConfirmed(Guid id)
        {
            var booking = await _bookingRepository.GetByIdAsync(id);
            if (booking == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (booking.UserId != userId) return Forbid();

            if (booking.Status == BookingStatus.Cancelled)
            {
                return RedirectToAction(nameof(MyBookings));
            }

            var now = _dateTimeService.Now;
            var timeToDeparture = booking.Trip.DepartureTime - now;
            if (timeToDeparture.TotalSeconds <= 0)
            {
                return BadRequest("لا يمكن إلغاء حجز لرحلة قد بدأت بالفعل.");
            }

            booking.Status = BookingStatus.Cancelled;
            await _bookingRepository.UpdateAsync(booking);

            // Calculate refund details
            decimal deductionPercentage = timeToDeparture.TotalHours <= 24 ? 25 : 10;
            decimal refundAmount = booking.Price * (1 - deductionPercentage / 100);

            var cancelMsg = $"تم إلغاء حجزك رقم {booking.Id.ToString().Substring(0, 8)} بنجاح. تم خصم {deductionPercentage}% وسيتم استرداد {refundAmount:F2} د.ك خلال أيام. شكراً لك! 🚂";
            await _smsService.SendSmsAsync(booking.PassengerPhone, cancelMsg);

            return RedirectToAction(nameof(MyBookings));
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBooking(Guid id)
        {
            var booking = await _bookingRepository.GetByIdAsync(id);
            if (booking == null) return NotFound();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (booking.UserId != userId) return Forbid();

            // Allow deletion of Cancelled OR PendingPayment bookings
            if (booking.Status != BookingStatus.Cancelled && booking.Status != BookingStatus.PendingPayment)
            {
                return BadRequest("يمكن حذف الحجوزات الملغية أو التي بانتظار الدفع فقط.");
            }

            await _bookingRepository.DeleteAsync(id);
            return RedirectToAction(nameof(MyBookings));
        }
    }
}
