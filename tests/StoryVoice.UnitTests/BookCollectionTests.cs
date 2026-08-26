using StoryVoice.Domain.Books;
using StoryVoice.Domain.Collections;

namespace StoryVoice.UnitTests;

public sealed class BookCollectionTests
{
    [Fact]
    public void Create_normalizes_name_and_description()
    {
        var ownerId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "  奇幻三部曲  ", "  一套完整的奇幻系列  ");

        Assert.Equal("奇幻三部曲", collection.Name);
        Assert.Equal("奇幻三部曲", collection.NormalizedName);
        Assert.Equal("一套完整的奇幻系列", collection.Description);
        Assert.Equal(ownerId, collection.OwnerId);
        Assert.Empty(collection.Books);
        Assert.Empty(collection.Shares);
    }

    [Fact]
    public void AddBook_rejects_foreign_owner_and_linked_placeholder_books()
    {
        var ownerId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "書冊", null);

        var foreignBook = Book.Create(Guid.NewGuid(), "外人之書", "作者", "zh-TW", "foreign.txt");
        Assert.Throws<InvalidOperationException>(() => collection.AddBook(foreignBook, "第一冊", 1));

        var externalBook = Book.CreateExternal(
            ownerId,
            "外部書目",
            "作者",
            "zh-TW",
            "legacy-external",
            "external-1",
            "https://example.invalid/book",
            coverImageUrl: null);
        Assert.Throws<InvalidOperationException>(() => collection.AddBook(externalBook, "第一冊", 1));
    }

    [Fact]
    public void AddBook_rejects_duplicate_membership_and_duplicate_sort_order()
    {
        var ownerId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "書冊", null);
        var book = Book.Create(ownerId, "第一冊", "作者", "zh-TW", "one.txt");
        collection.AddBook(book, "第一冊", 1);

        Assert.Throws<InvalidOperationException>(() => collection.AddBook(book, "重複冊次", 2));

        var secondBook = Book.Create(ownerId, "第二冊", "作者", "zh-TW", "two.txt");
        Assert.Throws<InvalidOperationException>(() => collection.AddBook(secondBook, "第二冊", 1));
    }

    [Fact]
    public void UpdateBook_reorders_and_relabels_existing_membership()
    {
        var ownerId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "書冊", null);
        var firstBook = Book.Create(ownerId, "第一冊", "作者", "zh-TW", "one.txt");
        var secondBook = Book.Create(ownerId, "第二冊", "作者", "zh-TW", "two.txt");
        collection.AddBook(firstBook, "第一冊", 1);
        collection.AddBook(secondBook, "第二冊", 2);

        collection.UpdateBook(firstBook.Id, "卷一", 5);

        var membership = Assert.Single(collection.Books, book => book.BookId == firstBook.Id);
        Assert.Equal("卷一", membership.VolumeLabel);
        Assert.Equal(5, membership.SortOrder);

        Assert.Throws<InvalidOperationException>(() => collection.UpdateBook(secondBook.Id, "卷二", 5));
        Assert.Throws<InvalidOperationException>(() => collection.UpdateBook(Guid.NewGuid(), "不存在", 9));
    }

    [Fact]
    public void RemoveBook_removes_membership_and_rejects_unknown_book()
    {
        var ownerId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "書冊", null);
        var book = Book.Create(ownerId, "第一冊", "作者", "zh-TW", "one.txt");
        collection.AddBook(book, "第一冊", 1);

        collection.RemoveBook(book.Id);

        Assert.Empty(collection.Books);
        Assert.Throws<InvalidOperationException>(() => collection.RemoveBook(book.Id));
    }

    [Fact]
    public void Rename_and_update_description_change_display_values()
    {
        var collection = BookCollection.Create(Guid.NewGuid(), "書冊", "原始描述");

        collection.Rename("  重新命名的書冊  ");
        collection.UpdateDescription("  新的描述  ");

        Assert.Equal("重新命名的書冊", collection.Name);
        Assert.Equal("重新命名的書冊", collection.NormalizedName);
        Assert.Equal("新的描述", collection.Description);

        collection.UpdateDescription(null);
        Assert.Null(collection.Description);
    }

    [Fact]
    public void AddShare_rejects_self_share_and_duplicate_grantee()
    {
        var ownerId = Guid.NewGuid();
        var granteeId = Guid.NewGuid();
        var collection = BookCollection.Create(ownerId, "書冊", null);

        Assert.Throws<InvalidOperationException>(() => collection.AddShare(ownerId, "owner@example.com"));

        var share = collection.AddShare(granteeId, "friend@example.com");
        Assert.Equal(granteeId, share.GranteeUserId);
        Assert.Equal("friend@example.com", share.GranteeEmail);
        Assert.Equal(ownerId, share.OwnerId);
        Assert.Equal(collection.Id, share.CollectionId);

        Assert.Throws<InvalidOperationException>(() => collection.AddShare(granteeId, "friend@example.com"));
    }

    [Fact]
    public void RevokeShare_removes_grant_and_rejects_unknown_share()
    {
        var collection = BookCollection.Create(Guid.NewGuid(), "書冊", null);
        var share = collection.AddShare(Guid.NewGuid(), "friend@example.com");

        collection.RevokeShare(share.Id);

        Assert.Empty(collection.Shares);
        Assert.Throws<InvalidOperationException>(() => collection.RevokeShare(share.Id));
    }
}
