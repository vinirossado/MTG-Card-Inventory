using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using MTG_Inventory.Dtos;
using MTG_Inventory.Models;

namespace MTG_Inventory.Repository;

public class CardRepository(AppDbContext context)
{
    public async Task Add(IList<Card> cards)
    {
        await context.Card.AddRangeAsync(cards);
        await context.SaveChangesAsync();
    }

    public async Task Add(Card card)
    {
        await context.Card.AddAsync(card);
        await context.SaveChangesAsync();
    }

    public async Task<List<Card>> Get()
    {
        return await context.Card.AsNoTracking().ToListAsync();
    }
    
    public async Task<List<Card>> GetCardsWithNoImage()
    {
        return await context.Card.Where(x=> x.ImageUri == null).ToListAsync();
    }

    public List<FilteredCard> GetMissingCards(IList<Card>? cardsFromFile, IList<Card> allCardsInDb)
    {
        if (cardsFromFile == null) return [];

        var missingCards = cardsFromFile
            .Where(card => allCardsInDb.All(dbCard => dbCard.Name != card.Name))
            .Select(card => new FilteredCard
            {
                Name = card.Name,
                Quantity = 1
            }).OrderBy(x => x.Name)
            .ToList();

        missingCards.AddRange(cardsFromFile
            .Where(card => allCardsInDb.Any(dbCard =>
                dbCard.Name == card.Name &&
                card.Quantity > dbCard.Quantity - dbCard.InUse))
            .Select(card =>
            {
                var dbCard = allCardsInDb.FirstOrDefault(dbCard => dbCard.Name == card.Name);
                return new FilteredCard
                {
                    Name = card.Name,
                    Quantity = (card.Quantity - ((dbCard?.Quantity ?? 0) - (dbCard?.InUse ?? 0)))!
                };
            }).OrderBy(x => x.Name)
            .ToList());

        return missingCards;
    }

    public async void Remove(IList<Card> cards)
    {
        context.Card.RemoveRange(cards);
        await context.SaveChangesAsync();
    }
    
    public List<FilteredCard> GetFoundCards(IList<Card> cardsFromFile, IList<Card> allCardsInDb)
    {
        return cardsFromFile
            .Where(card => allCardsInDb.Any(dbCard => dbCard.Name == card.Name && card.Quantity <= dbCard.Quantity - dbCard.InUse))
            .Select(card => new FilteredCard
            {
                Name = card.Name,
                Quantity = card.Quantity
            }).OrderBy(x => x.Name)
            .ToList();
    }
    
    public async Task<IList<Card>> GetMissingCardsToImport(IList<Card> cards)
    {
        var allCardsInDatabase = await context.Card
            .Select(dbCard => new Card
            {
                Name = dbCard.Name.ToLowerInvariant(),
                CardNumber = dbCard.CardNumber
            })
            .ToListAsync();

        List<Card> missingCards = [];

        var dbCardLookup = allCardsInDatabase
            .GroupBy(card => new { card?.Name})
            .ToDictionary(group => group.Key, group => group.Sum(card => card.Quantity));
        
        foreach (var card in cards)
        {
            var cardKey = new
            {
                Name = card.Name.ToLowerInvariant(),
            };

            // Check for exact match
            if (!dbCardLookup.ContainsKey(cardKey))
            {
                missingCards.Add(card);
            }
            else
            {
                dbCardLookup[cardKey] += card.Quantity;
            }
        }

        foreach (var card in cards)
        {
            if (!missingCards.Contains(card) && // Avoid adding duplicates
                !allCardsInDatabase.Any(dbCard =>
                    (dbCard.Name.Contains($"{card.Name} // ", StringComparison.CurrentCultureIgnoreCase) ||
                     dbCard.Name.Contains($" // {card.Name}", StringComparison.CurrentCultureIgnoreCase)) &&
                    dbCard.Quantity == card.Quantity))
            {
                missingCards.Add(card);
            }
        }

        Console.WriteLine($"Total no banco: {allCardsInDatabase.Count}, Não na lista: {missingCards.Count}");
    
        return missingCards;
    }

    public async Task<IList<Card>> DuplicatedCardsToBeRemoved()
    {
        var duplicateGroups = await context.Card
            .GroupBy(card => new
            {
                card.Name,
                card.Language,
                card.ExpansionName,
                card.CardNumber,
                card.Quantity,
                card.Foil
            })
            .Where(group => group.Count() > 1) // Filter duplicate groups
            .Select(group => new
            {
                Key = group.Key,
                Duplicates = group.Skip(1).ToList() // Skip(1) will not be translated, workaround needed
            })
            .ToListAsync();

        // Flatten the list to get all duplicate cards
        var duplicatedCards = duplicateGroups
            .SelectMany(group => group.Duplicates)
            .ToList();

        return duplicatedCards;
    }
    
    public async Task CardsToBeRemovedFromDb(IList<Card> cards)
    {
        var allCardsInDatabase = await context.Card
            .Select(dbCard => new Card
            {
                Id = dbCard.Id,
                Name = dbCard.Name.Trim().ToUpper(),
                Quantity = dbCard.Quantity,
                ExpansionName = dbCard.ExpansionName!.Trim().ToUpper()
            })
            .ToListAsync();
    
        var cardsToBeRemoved = allCardsInDatabase.Where(dbCard => !cards.Any(x =>
            dbCard.Name.ToLowerInvariant().Trim().Equals(x.Name.ToLowerInvariant().Trim(), StringComparison.CurrentCultureIgnoreCase) &&
            dbCard.ExpansionName?.ToUpper() == x.ExpansionName?.Trim().ToUpper() &&
            dbCard.Quantity == x.Quantity
        )).ToList();
    
        Console.WriteLine($"Total no banco: {allCardsInDatabase.Count}, Removidos: {cardsToBeRemoved.Count}");
    
        context.Card.RemoveRange(cardsToBeRemoved);
        await context.SaveChangesAsync();
    }
    
    public async Task Update(Card card)
    {
        context.Card.Update(card);
        await context.SaveChangesAsync();
    }

    public async Task Update(IList<Card> cards)
    {
        context.Card.UpdateRange(cards);
        await context.SaveChangesAsync();
    }
    
    public async Task<PagedResponseKeyset<Card>> GetCardsWithPagination(
        int reference, int pageSize, CardFilterDto filters)
    {
        var query = context.Card.AsQueryable();

        if (filters?.Name != null || filters?.ColorIdentity != null || filters?.TypeLine != null || filters?.CMC != null)
        {
            if (!string.IsNullOrWhiteSpace(filters?.Name))
                query = query.Where(card => card.Name.Contains(filters.Name));

            if (!string.IsNullOrWhiteSpace(filters?.ColorIdentity))
                query = query.Where(card => card.ColorIdentity != null 
                                            && card.ColorIdentity.ToUpper() == filters.ColorIdentity.ToUpper());

            if (!string.IsNullOrWhiteSpace(filters?.TypeLine))
                query = query.Where(card => card.TypeLine != null 
                                            && card.TypeLine.ToUpper() == filters.TypeLine.ToUpper());
            
            if (filters?.IsCommander != null)
                query = query.Where(card => card.IsCommander != null 
                                            && card.IsCommander == filters.IsCommander);

            if (filters.CMC.HasValue)
                query = query.Where(card => card.CMC == filters.CMC.Value);
        }

        var paginatedCards = await query
            .OrderBy(card => card.Id)
            .Where(card => card.Id > reference)
            .Take(pageSize)
            .ToListAsync();

        var newReference = paginatedCards.Any() ? paginatedCards.Last().Id : 0;

        return new(paginatedCards, newReference);
    }
}