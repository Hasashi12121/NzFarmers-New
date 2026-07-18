using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using NZFarmers.Areas.Identity.Data;
using NZFarmers.Data;
using NZFarmers.Models;

namespace NZFarmers.Controllers
{
    public class RatingsController : Controller
    {
        private readonly NZFarmersContext _context;
        private readonly UserManager<NZFarmersUser> _userManager;

        public RatingsController(NZFarmersContext context, UserManager<NZFarmersUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // GET: Ratings — public reviews wall
        public async Task<IActionResult> Index()
        {
            var ratings = await _context.Ratings
                .Include(r => r.Farmer)
                .Include(r => r.User)
                .OrderByDescending(r => r.CreatedAt)
                .AsNoTracking()
                .ToListAsync();

            return View(ratings);
        }

        // GET: Ratings/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null) return NotFound();

            var rating = await _context.Ratings
                .Include(r => r.Farmer)
                .Include(r => r.User)
                .FirstOrDefaultAsync(m => m.RatingID == id);

            if (rating == null) return NotFound();

            return View(rating);
        }

        // GET: Ratings/Create?farmerId=5
        [Authorize]
        public IActionResult Create(int? farmerId)
        {
            ViewData["FarmerID"] = new SelectList(_context.Farmers.OrderBy(f => f.FarmName), "FarmerID", "FarmName", farmerId);
            return View(new Rating { FarmerID = farmerId ?? 0 });
        }

        // POST: Ratings/Create
        // Always posted as the logged-in user. One review per farmer per user —
        // reviewing again just updates the existing review.
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Create([Bind("FarmerID,RatingValue,Comment")] Rating rating)
        {
            var userId = _userManager.GetUserId(User);
            if (string.IsNullOrEmpty(userId))
                return Challenge();

            rating.UserId = userId;
            rating.CreatedAt = DateTime.UtcNow;

            ModelState.Remove("UserId");
            ModelState.Remove("User");
            ModelState.Remove("Farmer");

            var farmer = await _context.Farmers.FindAsync(rating.FarmerID);
            if (farmer == null)
                ModelState.AddModelError("FarmerID", "Please choose a farmer to review.");

            // Farmers can't review their own farm.
            if (farmer != null && farmer.UserID == userId)
            {
                TempData["Error"] = "You can't review your own farm.";
                return RedirectToAction("Details", "Farmers", new { id = rating.FarmerID });
            }

            if (ModelState.IsValid)
            {
                var existing = await _context.Ratings
                    .FirstOrDefaultAsync(r => r.UserId == userId && r.FarmerID == rating.FarmerID);

                if (existing != null)
                {
                    existing.RatingValue = rating.RatingValue;
                    existing.Comment = rating.Comment;
                    existing.CreatedAt = DateTime.UtcNow;
                    TempData["Success"] = "Your review has been updated.";
                }
                else
                {
                    _context.Add(rating);
                    TempData["Success"] = "Thanks for your review!";
                }

                await _context.SaveChangesAsync();
                return RedirectToAction("Details", "Farmers", new { id = rating.FarmerID });
            }

            ViewData["FarmerID"] = new SelectList(_context.Farmers.OrderBy(f => f.FarmName), "FarmerID", "FarmName", rating.FarmerID);
            return View(rating);
        }

        // GET: Ratings/Edit/5 — only the author or an admin
        [Authorize]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null) return NotFound();

            var rating = await _context.Ratings.Include(r => r.Farmer).FirstOrDefaultAsync(r => r.RatingID == id);
            if (rating == null) return NotFound();

            if (!CanManage(rating)) return Forbid();

            return View(rating);
        }

        // POST: Ratings/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> Edit(int id, [Bind("RatingID,RatingValue,Comment")] Rating form)
        {
            var rating = await _context.Ratings.FirstOrDefaultAsync(r => r.RatingID == id);
            if (rating == null) return NotFound();

            if (!CanManage(rating)) return Forbid();

            ModelState.Remove("UserId");
            ModelState.Remove("User");
            ModelState.Remove("Farmer");
            ModelState.Remove("FarmerID");

            if (ModelState.IsValid)
            {
                rating.RatingValue = form.RatingValue;
                rating.Comment = form.Comment;
                rating.CreatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                TempData["Success"] = "Review updated.";
                return RedirectToAction("Details", "Farmers", new { id = rating.FarmerID });
            }

            return View(rating);
        }

        // GET: Ratings/Delete/5 — only the author or an admin
        [Authorize]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null) return NotFound();

            var rating = await _context.Ratings
                .Include(r => r.Farmer)
                .Include(r => r.User)
                .FirstOrDefaultAsync(m => m.RatingID == id);

            if (rating == null) return NotFound();

            if (!CanManage(rating)) return Forbid();

            return View(rating);
        }

        // POST: Ratings/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        [Authorize]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var rating = await _context.Ratings.FindAsync(id);
            if (rating == null) return RedirectToAction(nameof(Index));

            if (!CanManage(rating)) return Forbid();

            int farmerId = rating.FarmerID;
            _context.Ratings.Remove(rating);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Review deleted.";
            return RedirectToAction("Details", "Farmers", new { id = farmerId });
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private bool CanManage(Rating rating)
        {
            if (User.IsInRole("Admin")) return true;
            return rating.UserId == _userManager.GetUserId(User);
        }
    }
}
