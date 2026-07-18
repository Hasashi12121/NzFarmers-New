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
    public class ShoppingCartItemsController : Controller
    {
        private readonly NZFarmersContext _context;
        private readonly UserManager<NZFarmersUser> _userManager;

        public ShoppingCartItemsController(NZFarmersContext context, UserManager<NZFarmersUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // GET: ShoppingCartItems
        // The user's cart page.
        public async Task<IActionResult> Index()
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var cartItems = await _context.ShoppingCartItems
                .Include(ci => ci.FarmerProduct)
                    .ThenInclude(fp => fp.Farmer)
                .Where(ci => ci.UserID == userId)
                .OrderBy(ci => ci.CreatedAt)
                .ToListAsync();

            return View(cartItems);
        }

        // POST: ShoppingCartItems/AddToCart
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddToCart(int farmerProductId, int quantity = 1, string? returnUrl = null)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var product = await _context.FarmerProducts.FindAsync(farmerProductId);
            if (product == null)
                return NotFound();

            if (product.Stock <= 0)
            {
                TempData["Error"] = $"{product.ProductName} is out of stock.";
                return RedirectToLocal(returnUrl);
            }

            if (quantity < 1) quantity = 1;

            var existingItem = await _context.ShoppingCartItems
                .FirstOrDefaultAsync(ci => ci.FarmerProductID == farmerProductId && ci.UserID == userId);

            int newQuantity = (existingItem?.Quantity ?? 0) + quantity;

            // Never let the cart hold more than what's in stock.
            if (newQuantity > product.Stock)
            {
                newQuantity = product.Stock;
                TempData["Error"] = $"Only {product.Stock} of {product.ProductName} available — cart adjusted.";
            }
            else
            {
                TempData["Success"] = $"{product.ProductName} added to your cart.";
            }

            if (existingItem != null)
            {
                existingItem.Quantity = newQuantity;
            }
            else
            {
                _context.ShoppingCartItems.Add(new ShoppingCartItem
                {
                    UserID = userId,
                    FarmerProductID = farmerProductId,
                    Quantity = newQuantity,
                    CreatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
            return RedirectToLocal(returnUrl);
        }

        // POST: ShoppingCartItems/UpdateQuantity
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateQuantity(int cartItemId, int quantity)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var cartItem = await _context.ShoppingCartItems
                .Include(ci => ci.FarmerProduct)
                .FirstOrDefaultAsync(ci => ci.ShoppingCartItemID == cartItemId && ci.UserID == userId);

            if (cartItem == null)
                return RedirectToAction(nameof(Index));

            if (quantity <= 0)
            {
                _context.ShoppingCartItems.Remove(cartItem);
            }
            else
            {
                if (quantity > cartItem.FarmerProduct.Stock)
                {
                    quantity = cartItem.FarmerProduct.Stock;
                    TempData["Error"] = $"Only {cartItem.FarmerProduct.Stock} of {cartItem.FarmerProduct.ProductName} available.";
                }
                cartItem.Quantity = Math.Max(1, quantity);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        // POST: ShoppingCartItems/RemoveItem
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveItem(int cartItemId)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var cartItem = await _context.ShoppingCartItems
                .FirstOrDefaultAsync(ci => ci.ShoppingCartItemID == cartItemId && ci.UserID == userId);

            if (cartItem != null)
            {
                _context.ShoppingCartItems.Remove(cartItem);
                await _context.SaveChangesAsync();
            }

            return RedirectToAction(nameof(Index));
        }

        // POST: ShoppingCartItems/Checkout
        // Creates a Stripe Checkout Session and sends the user to Stripe's hosted payment page.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Checkout()
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            var cartItems = await _context.ShoppingCartItems
                .Include(ci => ci.FarmerProduct)
                .Where(ci => ci.UserID == userId)
                .ToListAsync();

            if (!cartItems.Any())
                return RedirectToAction(nameof(Index));

            // Final stock check before taking payment.
            foreach (var item in cartItems)
            {
                if (item.Quantity > item.FarmerProduct.Stock)
                {
                    TempData["Error"] = $"Only {item.FarmerProduct.Stock} of {item.FarmerProduct.ProductName} left in stock. Please update your cart.";
                    return RedirectToAction(nameof(Index));
                }
            }

            var user = await _userManager.GetUserAsync(User);

            // {CHECKOUT_SESSION_ID} is replaced by Stripe — PaymentSuccess uses it to
            // verify the payment actually happened before creating the order.
            var successUrl = Url.Action("PaymentSuccess", "Orders", null, Request.Scheme)
                             + "?session_id={CHECKOUT_SESSION_ID}";

            var sessionOptions = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                Mode = "payment",
                SuccessUrl = successUrl,
                CancelUrl = Url.Action("PaymentCanceled", "Orders", null, Request.Scheme),
                CustomerEmail = user?.Email,
                LineItems = new List<SessionLineItemOptions>()
            };

            foreach (var item in cartItems)
            {
                sessionOptions.LineItems.Add(new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "nzd",
                        UnitAmount = (long)(item.FarmerProduct.Price * 100), // Stripe uses cents
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = item.FarmerProduct.ProductName
                        }
                    },
                    Quantity = item.Quantity
                });
            }

            var sessionService = new SessionService();
            Session stripeSession = await sessionService.CreateAsync(sessionOptions);

            return Redirect(stripeSession.Url);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private IActionResult RedirectToLocal(string? returnUrl)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction(nameof(Index));
        }
    }
}
