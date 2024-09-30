using Backend.Data;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Threading.Tasks;
using MongoDB.Bson; // Make sure this is included
using System.Security.Claims; // Include this for Claims

public class UserService : IUserService
{
    private readonly IMongoCollection<User> _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtService _jwtService;

    public UserService(IMongoDbContext dbContext, IPasswordHasher passwordHasher, IJwtService jwtService)
    {
        _users = dbContext.Users;
        _passwordHasher = passwordHasher;
        _jwtService = jwtService; // Inject JWT service
    }

    // Authenticate method using email and password and Role
    public async Task<(User user, string jwtToken, string refreshToken)> Authenticate(string email, string password, string role)
    {
        // Find the user by email
        var user = await _users.Find(u => u.Email == email && (role == "Customer" ? u.Role == "Customer" : u.Role != "Customer")).FirstOrDefaultAsync();

        // Check if the user exists and if the password is correct
        if (user == null || !_passwordHasher.Verify(password, user.PasswordHash))
            return (null, null, null); // Return null if user not found or password is incorrect

        // Generate JWT and refresh token
        var jwtToken = _jwtService.GenerateJwt(user);
        var refreshToken = _jwtService.GenerateRefreshToken();
        _jwtService.StoreRefreshToken(user.Id, refreshToken); // Store the refresh token

        return (user, jwtToken, refreshToken); // Return the authenticated user and tokens
    }

    // Register method
    public async Task<User> Register(string email, string username, string password, string role, string address, int mobileNumber)
    {
        // Check if the role is "Customer" and ensure the email is unique among customers
        if (role == "Customer")
        {
            var existingCustomer = await _users.Find(u => u.Email == email && u.Role == "Customer").FirstOrDefaultAsync();
            if (existingCustomer != null) return null; // Customer already exists with the same email
        }

        if (role != "Customer")
        {
            // Check if the email is already used by another user who is not a customer
            var existingUser = await _users.Find(u => u.Email == email && u.Role != "Customer").FirstOrDefaultAsync();
            if (existingUser != null) return null; // Email is already in use by another user
        }

        var user = new User
        {
            Email = email,
            Username = username,
            PasswordHash = _passwordHasher.Hash(password),
            Role = role,
            IsApproved = role == "Customer" ? false : true, // Only unapproved for "Customer" role
            Address = address,
            MobileNumber = mobileNumber,
            VendorDetails = role == "Vendor" ? new Vendor() : null // Set Vendor details if role is Vendor
        };

        await _users.InsertOneAsync(user);

        // If user is a customer, send notification to CSR for approval
        if (user.Role == "Customer")
        {
            await NotifyCsrForApproval(user); // Notify CSR about pending customer approval
        }

        return user;
    }

    // ApproveCustomer method
    public async Task<bool> ApproveCustomer(string customerId, bool isApproved)
    {
        var update = Builders<User>.Update.Set(u => u.IsApproved, isApproved);
        var result = await _users.UpdateOneAsync(u => u.Id == customerId && u.Role == "Customer", update);
        return result.ModifiedCount > 0; // Return true if the update was successful
    }

    // Notify CSR method
    private async Task NotifyCsrForApproval(User user)
    {
        Console.WriteLine($"New customer registration pending approval: {user.Id}");
        // Implement push notification or other notification methods here
    }

    // Check if the user is an administrator
    public async Task<bool> IsAdministrator(string userId)
    {
        var user = await _users.Find(u => u.Id == userId).FirstOrDefaultAsync();
        return user?.Role == "Administrator";
    }

    // Get a user by ID
    public async Task<User> GetUserById(string userId)
    {
        return await _users.Find(u => u.Id == userId).FirstOrDefaultAsync();
    }

    // Get all users
    public async Task<IEnumerable<User>> GetAllUsers()
    {
        return await _users.Find(_ => true).ToListAsync(); // Return as IEnumerable<User>
    }

    // Update user information
    public async Task<bool> UpdateUser(string userId, User updatedUser)
    {
        var result = await _users.ReplaceOneAsync(u => u.Id == userId, updatedUser);
        return result.ModifiedCount > 0;
    }

    // Delete a user
    public async Task<bool> DeleteUser(string userId)
    {
        var result = await _users.DeleteOneAsync(u => u.Id == userId);
        return result.DeletedCount > 0;
    }

    // New method to get a user by email
    public async Task<User> GetUserByEmail(string email)
    {
        return await _users.Find(u => u.Email == email).FirstOrDefaultAsync();
    }

    public async Task<User> AddCommentAndUpdateRating(string vendorId, Comment newComment)
    {
        // Set a new unique ID for the comment
        newComment.Id = ObjectId.GenerateNewId().ToString(); // Generate a new ObjectId
        // Find the vendor by ID
        var vendor = await _users.Find(u => u.Id == vendorId && u.Role == "Vendor").FirstOrDefaultAsync();

        if (vendor == null || vendor.VendorDetails == null) return null; // Vendor not found

        // Add the new comment to the vendor's comment list
        vendor.VendorDetails.Comments.Add(newComment);

        // Recalculate the average rating
        var totalRating = vendor.VendorDetails.Comments.Sum(c => c.Rating);
        var commentCount = vendor.VendorDetails.Comments.Count;
        vendor.VendorDetails.AverageRating = totalRating / commentCount;

        // Update the vendor in the database
        var updateResult = await _users.ReplaceOneAsync(u => u.Id == vendorId, vendor);

        if (updateResult.ModifiedCount > 0)
        {
            return vendor; // Return the updated vendor
        }

        return null; // Return null if the update failed
    }

    public async Task<User> UpdateComment(string vendorId, string commentId, string commentText)
    {
        // Find the vendor by ID
        var vendor = await _users.Find(u => u.Id == vendorId && u.Role == "Vendor").FirstOrDefaultAsync();

        if (vendor == null || vendor.VendorDetails == null) return null; // Vendor not found

        // Find the specific comment by ID
        var comment = vendor.VendorDetails.Comments.FirstOrDefault(c => c.Id == commentId);
        if (comment == null) return null; // Comment not found

        // Update the comment text
        comment.CommentText = commentText;

        // Recalculate the average rating after the update
        var totalRating = vendor.VendorDetails.Comments.Sum(c => c.Rating);
        var commentCount = vendor.VendorDetails.Comments.Count;
        vendor.VendorDetails.AverageRating = totalRating / commentCount;

        // Update the vendor in the database
        var updateResult = await _users.ReplaceOneAsync(u => u.Id == vendorId, vendor);

        if (updateResult.ModifiedCount > 0)
        {
            return vendor; // Return the updated vendor
        }

        return null; // Return null if the update failed
    }
}
