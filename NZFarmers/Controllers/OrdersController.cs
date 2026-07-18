using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NZFarmers.Areas.Identity.Data;
using NZFarmers.Data;
using NZFarmers.Models;
using Stripe.Checkout;

namespace NZFarmers.Controllers
{
    [Authorize]
    public class OrdersController : Controller
    {
        private readonly NZFarmersContext _context;
        private readonly UserManager<NZFarmersUser> _userManager;

        public OrdersController(NZFarmersContext context, UserManager<NZFarmersUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // GET: Orders
        // Admins see every order on the platform; everyone else goes to their own order history.
        public async Task<IActionResult> Index()
        {
            if (!User.IsInRole("Admin"))
                return RedirectToAction(nameof(MyOrders));

            var orders = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return View(orders);
        }

        // GET: Orders/MyOrders
        // The logged-in user's own purchase history.
        public async Task<IActionResult> MyOrders()
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var orders = await _context.Orders
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                        .ThenInclude(fp => fp.Farmer)
                .Where(o => o.UserID == userId)
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            return View(orders);
        }

        // GET: Orders/Selling
        // Farmer dashboard: every order that contains at least one of this farmer's products.
        [Authorize(Roles = "Farmer,Admin")]
        public async Task<IActionResult> Selling()
        {
            var userId = _userManager.GetUserId(User);
            var farmer = await _context.Farmers.FirstOrDefaultAsync(f => f.UserID == userId);

            if (farmer == null)
            {
                TempData["Error"] = "Create your farmer profile first to see your sales.";
                return RedirectToAction("Create", "Farmers");
            }

            var orders = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                .Where(o => o.OrderDetails!.Any(od => od.FarmerProduct.FarmerID == farmer.FarmerID))
                .OrderByDescending(o => o.CreatedAt)
                .ToListAsync();

            ViewBag.FarmerID = farmer.FarmerID;
            ViewBag.FarmName = farmer.FarmName;
            return View(orders);
        }

        // GET: Orders/Details/5
        // Visible to: the buyer, an admin, or a farmer who has products in the order.
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var order = await _context.Orders
                .Include(o => o.User)
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                        .ThenInclude(fp => fp.Farmer)
                .FirstOrDefaultAsync(m => m.OrderID == id);

            if (order == null) return NotFound();

            if (!await CanViewOrderAsync(order))
                return Forbid();

            var payment = await _context.PaymentDetails.FirstOrDefaultAsync(p => p.OrderID == order.OrderID);
            ViewBag.Payment = payment;
            ViewBag.CanManage = await CanManageOrderAsync(order);

            return View(order);
        }

        // POST: Orders/UpdateStatus
        // Farmers (with products in the order) and admins can move an order through its lifecycle.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize(Roles = "Farmer,Admin")]
        public async Task<IActionResult> UpdateStatus(int orderId, OrderStatus status, string? returnUrl)
        {
            var order = await _context.Orders
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                .FirstOrDefaultAsync(o => o.OrderID == orderId);

            if (order == null) return NotFound();

            if (!await CanManageOrderAsync(order))
                return Forbid();

            order.Status = status;
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Order #{order.OrderID} marked as {status}.";

            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction(nameof(Selling));
        }

        // GET: Orders/PaymentSuccess?session_id=...
        // Stripe redirects here after a successful payment. We verify the session was
        // actually paid before creating the order, so nobody can fake a purchase by
        // visiting this URL directly.
        [HttpGet]
        public async Task<IActionResult> PaymentSuccess(string? session_id)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var cartItems = await _context.ShoppingCartItems
                .Include(ci => ci.FarmerProduct)
                .Where(ci => ci.UserID == userId)
                .ToListAsync();

            // Cart already cleared (e.g. page refresh after order creation).
            if (!cartItems.Any())
                return RedirectToAction(nameof(MyOrders));

            // Verify the Stripe checkout session really was paid.
            if (string.IsNullOrEmpty(session_id))
            {
                TempData["Error"] = "We couldn't verify your payment. Please try again.";
                return RedirectToAction("Index", "ShoppingCartItems");
            }

            Session stripeSession;
            try
            {
                var sessionService = new SessionService();
                stripeSession = await sessionService.GetAsync(session_id);
            }
            catch (Exception)
            {
                TempData["Error"] = "We couldn't verify your payment. Please try again.";
                return RedirectToAction("Index", "ShoppingCartItems");
            }

            if (stripeSession.PaymentStatus != "paid")
            {
                TempData["Error"] = "Your payment was not completed.";
                return RedirectToAction(nameof(PaymentCanceled));
            }

            decimal totalPrice = cartItems.Sum(item => item.Quantity * item.FarmerProduct.Price);

            var order = new Order
            {
                UserID = userId,
                TotalPrice = totalPrice,
                Status = OrderStatus.Processing,
                CreatedAt = DateTime.UtcNow
            };

            _context.Orders.Add(order);
            await _context.SaveChangesAsync(); // Generates OrderID for the foreign keys below.

            foreach (var item in cartItems)
            {
                _context.OrderDetails.Add(new OrderDetail
                {
                    OrderID = order.OrderID,
                    FarmerProductID = item.FarmerProductID,
                    Quantity = item.Quantity,
                    Subtotal = item.Quantity * item.FarmerProduct.Price
                });

                // Reduce stock, never below zero.
                item.FarmerProduct.Stock = Math.Max(0, item.FarmerProduct.Stock - item.Quantity);
            }

            // Record the payment against the order.
            _context.PaymentDetails.Add(new PaymentDetail
            {
                UserID = userId,
                OrderID = order.OrderID,
                Amount = totalPrice,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                CreatedAt = DateTime.UtcNow
            });

            _context.ShoppingCartItems.RemoveRange(cartItems);
            await _context.SaveChangesAsync();

            // Reload with details for the confirmation page.
            var confirmedOrder = await _context.Orders
                .Include(o => o.OrderDetails!)
                    .ThenInclude(od => od.FarmerProduct)
                        .ThenInclude(fp => fp.Farmer)
                .FirstAsync(o => o.OrderID == order.OrderID);

            return View(confirmedOrder);
        }

        // GET: Orders/PaymentCanceled
        [HttpGet]
        public IActionResult PaymentCanceled()
        {
            return View();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private async Task<bool> CanViewOrderAsync(Order order)
        {
            var userId = _userManager.GetUserId(User);

            if (User.IsInRole("Admin")) return true;
            if (order.UserID == userId) return true;

            return await IsSellingFarmerAsync(order);
        }

        private async Task<bool> CanManageOrderAsync(Order order)
        {
            if (User.IsInRole("Admin")) return true;
            if (!User.IsInRole("Farmer")) return false;

            return await IsSellingFarmerAsync(order);
        }

        private async Task<bool> IsSellingFarmerAsync(Order order)
        {
            var userId = _userManager.GetUserId(User);
            var farmer = await _context.Farmers.FirstOrDefaultAsync(f => f.UserID == userId);
            if (farmer == null) return false;

            return order.OrderDetails != null &&
                   order.OrderDetails.Any(od => od.FarmerProduct != null && od.FarmerProduct.FarmerID == farmer.FarmerID);
        }
    }
}
