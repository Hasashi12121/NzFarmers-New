using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using NZFarmers.Areas.Identity.Data;
using NZFarmers.Data;
using NZFarmers.Models;

namespace NZFarmers.Controllers
{

    public class FarmerProductsController : Controller
    {
        private readonly NZFarmersContext _context;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly UserManager<NZFarmersUser> _userManager;
        private const int PageSize = 12;

        public FarmerProductsController(
            NZFarmersContext context,
            IWebHostEnvironment webHostEnvironment,
            UserManager<NZFarmersUser> userManager)
        {
            _context = context;
            _webHostEnvironment = webHostEnvironment;
            _userManager = userManager;
        }

        // Returns the farmer profile linked to the currently logged-in user, or null
        private async Task<Farmers?> GetCurrentFarmerAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return null;
            return await _context.Farmers.FirstOrDefaultAsync(f => f.UserID == user.Id);
        }

        // Returns true if the current user is an Admin OR owns the given product
        private async Task<bool> CanManageProductAsync(FarmerProduct product)
        {
            if (User.IsInRole("Admin")) return true;
            var farmer = await GetCurrentFarmerAsync();
            return farmer != null && farmer.FarmerID == product.FarmerID;
        }

        // GET: FarmerProducts
        public async Task<IActionResult> Index(
            string searchString,
            string categoryFilter,
            int? pageNumber)
        {
            var query = _context.FarmerProducts
                .Include(f => f.Farmer)
                .AsQueryable();

            // Search functionality
            if (!string.IsNullOrEmpty(searchString))
            {
                query = query.Where(p =>
                    p.ProductName.Contains(searchString) ||
                    p.Farmer.FarmName.Contains(searchString) ||
                    p.Farmer.City.Contains(searchString));
            }

            // Category filter
            if (!string.IsNullOrEmpty(categoryFilter) && Enum.TryParse<ProductCategory>(categoryFilter, out var parsedCategory))
            {
                query = query.Where(p => p.Category == parsedCategory);
            }

            // Order by product name for consistent pagination
            query = query.OrderBy(p => p.ProductName);

            // Store current filters for view
            ViewData["CurrentSearch"] = searchString;
            ViewData["CurrentCategory"] = categoryFilter;
            ViewData["CategoryList"] = new SelectList(Enum.GetValues(typeof(ProductCategory)));

            // Apply pagination
            int pageIndex = pageNumber ?? 1;
            var paginatedProducts = await PaginatedList<FarmerProduct>.CreateAsync(
                query.AsNoTracking(), pageIndex, PageSize);

            return View(paginatedProducts);
        }

        // GET: FarmerProducts/Details/5
        public async Task<IActionResult> Details(int? id)
        {
            if (id == null)
                return NotFound();

            var farmerProduct = await _context.FarmerProducts
                .Include(f => f.Farmer)
                .FirstOrDefaultAsync(m => m.FarmerProductID == id);

            if (farmerProduct == null)
                return NotFound();

            // Related products from the same category (excluding this one)
            ViewBag.RelatedProducts = await _context.FarmerProducts
                .Include(p => p.Farmer)
                .Where(p => p.Category == farmerProduct.Category && p.FarmerProductID != farmerProduct.FarmerProductID)
                .OrderBy(p => Guid.NewGuid())
                .Take(4)
                .AsNoTracking()
                .ToListAsync();

            // Farmer's overall rating for the trust card
            var ratings = await _context.Ratings
                .Where(r => r.FarmerID == farmerProduct.FarmerID)
                .Select(r => r.RatingValue)
                .ToListAsync();
            ViewBag.FarmerAvgRating = ratings.Any() ? ratings.Average() : 0.0;
            ViewBag.FarmerRatingCount = ratings.Count;

            // Whether the current user may edit this product
            ViewBag.CanManage = User.Identity != null && User.Identity.IsAuthenticated
                && await CanManageProductAsync(farmerProduct);

            return View(farmerProduct);
        }

        // GET: FarmerProducts/Create
        [Authorize(Roles = "Admin,Farmer")]
        public async Task<IActionResult> Create()
        {
            ViewData["Category"] = new SelectList(Enum.GetValues(typeof(ProductCategory)));

            if (User.IsInRole("Admin"))
            {
                // Admins can assign any farmer
                ViewData["FarmerID"] = new SelectList(_context.Farmers, "FarmerID", "FarmName");
                return View();
            }

            // Farmers can only create products under their own profile
            var farmer = await GetCurrentFarmerAsync();
            if (farmer == null)
            {
                TempData["Error"] = "You need to create a Farmer Profile before listing products.";
                return RedirectToAction("Create", "Farmers");
            }

            // Pre-set the FarmerID so the form knows which farmer this belongs to
            var model = new FarmerProduct { FarmerID = farmer.FarmerID };
            ViewData["FarmerName"] = farmer.FarmName;
            return View(model);
        }

        // POST: FarmerProducts/Create
        [Authorize(Roles = "Admin,Farmer")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(FarmerProduct farmerProduct)
        {
            // Non-admins: always force FarmerID to be the current user's farmer
            if (!User.IsInRole("Admin"))
            {
                var farmer = await GetCurrentFarmerAsync();
                if (farmer == null)
                {
                    TempData["Error"] = "You need a Farmer Profile to list products.";
                    return RedirectToAction("Create", "Farmers");
                }
                farmerProduct.FarmerID = farmer.FarmerID;
            }

            // Clear any FarmerID validation error since we set it above
            ModelState.Remove("FarmerID");
            ModelState.Remove("Farmer");

            if (ModelState.IsValid)
            {
                if (farmerProduct.ImageFile != null)
                {
                    string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads");
                    Directory.CreateDirectory(uploadsFolder);
                    string uniqueFileName = Guid.NewGuid().ToString()
                        + "_" + Path.GetFileName(farmerProduct.ImageFile.FileName);
                    string filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await farmerProduct.ImageFile.CopyToAsync(fileStream);
                    }
                    farmerProduct.ImageURL = "/uploads/" + uniqueFileName;
                }

                _context.Add(farmerProduct);
                await _context.SaveChangesAsync();
                TempData["Success"] = "Product listed successfully!";
                return RedirectToAction(nameof(Index));
            }

            // Re-populate on validation failure
            ViewData["Category"] = new SelectList(Enum.GetValues(typeof(ProductCategory)), farmerProduct.Category);
            if (User.IsInRole("Admin"))
                ViewData["FarmerID"] = new SelectList(_context.Farmers, "FarmerID", "FarmName", farmerProduct.FarmerID);

            return View(farmerProduct);
        }

        // GET: FarmerProducts/Edit/
        [Authorize(Roles = "Admin,Farmer")]
        public async Task<IActionResult> Edit(int? id)
        {
            if (id == null)
                return NotFound();

            var farmerProduct = await _context.FarmerProducts.FindAsync(id);
            if (farmerProduct == null)
                return NotFound();

            if (!await CanManageProductAsync(farmerProduct))
                return Forbid();

            ViewData["FarmerID"] = new SelectList(_context.Farmers, "FarmerID", "FarmName", farmerProduct.FarmerID);
            ViewData["Category"] = new SelectList(Enum.GetValues(typeof(ProductCategory)), farmerProduct.Category);
            return View(farmerProduct);
        }

        // POST: FarmerProducts/Edit/
        [Authorize(Roles = "Admin,Farmer")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("FarmerProductID,FarmerID,ProductName,Description,Category,Price,Stock,ImageURL,ImageFile")] FarmerProduct farmerProduct)
        {
            if (id != farmerProduct.FarmerProductID)
                return NotFound();

            // Load the original to check ownership before accepting the update
            var original = await _context.FarmerProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.FarmerProductID == id);
            if (original == null)
                return NotFound();

            if (!await CanManageProductAsync(original))
                return Forbid();

            // Prevent non-admins from changing the FarmerID
            if (!User.IsInRole("Admin"))
                farmerProduct.FarmerID = original.FarmerID;

            ModelState.Remove("Farmer");

            if (ModelState.IsValid)
            {
                // Handle a replacement image upload; otherwise keep the existing one.
                if (farmerProduct.ImageFile != null)
                {
                    string uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads");
                    Directory.CreateDirectory(uploadsFolder);
                    string uniqueFileName = Guid.NewGuid().ToString()
                        + "_" + Path.GetFileName(farmerProduct.ImageFile.FileName);
                    string filePath = Path.Combine(uploadsFolder, uniqueFileName);
                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await farmerProduct.ImageFile.CopyToAsync(fileStream);
                    }
                    farmerProduct.ImageURL = "/uploads/" + uniqueFileName;
                }
                else if (string.IsNullOrEmpty(farmerProduct.ImageURL))
                {
                    farmerProduct.ImageURL = original.ImageURL;
                }

                try
                {
                    _context.Update(farmerProduct);
                    await _context.SaveChangesAsync();
                    TempData["Success"] = "Product updated successfully!";
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!FarmerProductExists(farmerProduct.FarmerProductID))
                        return NotFound();
                    else
                        throw;
                }
                return RedirectToAction(nameof(Index));
            }

            ViewData["FarmerID"] = new SelectList(_context.Farmers, "FarmerID", "FarmName", farmerProduct.FarmerID);
            ViewData["Category"] = new SelectList(Enum.GetValues(typeof(ProductCategory)), farmerProduct.Category);
            return View(farmerProduct);
        }

        // GET: FarmerProducts/Delete/5
        [Authorize(Roles = "Admin,Farmer")]
        public async Task<IActionResult> Delete(int? id)
        {
            if (id == null)
                return NotFound();

            var farmerProduct = await _context.FarmerProducts
                .Include(f => f.Farmer)
                .FirstOrDefaultAsync(m => m.FarmerProductID == id);

            if (farmerProduct == null)
                return NotFound();

            if (!await CanManageProductAsync(farmerProduct))
                return Forbid();

            return View(farmerProduct);
        }

        // POST: FarmerProducts/Delete/5
        [Authorize(Roles = "Admin,Farmer")]
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id)
        {
            var farmerProduct = await _context.FarmerProducts.FindAsync(id);
            if (farmerProduct == null)
                return NotFound();

            if (!await CanManageProductAsync(farmerProduct))
                return Forbid();

            _context.FarmerProducts.Remove(farmerProduct);
            await _context.SaveChangesAsync();
            TempData["Success"] = "Product deleted.";
            return RedirectToAction(nameof(Index));
        }

        private bool FarmerProductExists(int id)
        {
            return _context.FarmerProducts.Any(e => e.FarmerProductID == id);
        }
    }
}