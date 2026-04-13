using System.Collections.Concurrent;
using System.Text.Json;

namespace OCRReader.Learning;

/// <summary>
/// Manages persistent OCR corrections that improve accuracy over time.
/// Stores corrections in a local JSON file that survives app restarts.
/// </summary>
public class CorrectionStore
{
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, CorrectionEntry> _corrections;
    private readonly object _fileLock = new();
    
    /// <summary>Maximum number of corrections to store (prevents file bloat).</summary>
    public int MaxCorrections { get; set; } = 10000;
    
    /// <summary>Number of corrections currently stored.</summary>
    public int Count => _corrections.Count;
    
    public CorrectionStore(string storagePath)
    {
        _storagePath = storagePath;
        _corrections = new ConcurrentDictionary<string, CorrectionEntry>();
        Load();
    }
    
    /// <summary>
    /// Records a user correction for future auto-correction.
    /// </summary>
    /// <param name="ocrText">The text that OCR incorrectly extracted</param>
    /// <param name="correctedText">The correct text as confirmed by user</param>
    /// <param name="supermarket">The supermarket where this correction applies</param>
    public void RecordCorrection(string ocrText, string correctedText, string supermarket)
    {
        if (string.IsNullOrWhiteSpace(ocrText) || string.IsNullOrWhiteSpace(correctedText))
            return;
            
        var key = MakeKey(ocrText, supermarket);
        var normalizedOcr = ocrText.Trim().ToUpperInvariant();
        var normalizedCorrected = correctedText.Trim().ToUpperInvariant();
        
        _corrections.AddOrUpdate(
            key,
            _ => new CorrectionEntry
            {
                OcrText = normalizedOcr,
                CorrectedText = normalizedCorrected,
                Supermarket = supermarket.ToUpperInvariant(),
                Count = 1,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            },
            (_, existing) =>
            {
                existing.Count++;
                existing.LastSeen = DateTime.UtcNow;
                // Update corrected text if it's different (user changed their mind)
                if (existing.CorrectedText != normalizedCorrected)
                {
                    existing.CorrectedText = normalizedCorrected;
                    existing.Count = 1; // Reset count for new correction
                }
                return existing;
            });
        
        // Auto-save after recording
        Save();
    }
    
    /// <summary>
    /// Applies all learned corrections to the given text.
    /// </summary>
    public string ApplyCorrections(string text, string supermarket = "")
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
            
        var result = text;
        var market = supermarket.ToUpperInvariant();
        
        foreach (var entry in _corrections.Values)
        {
            // Apply supermarket-specific corrections
            if (string.Equals(entry.Supermarket, market, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Supermarket, "ALL", StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(entry.OcrText, entry.CorrectedText, StringComparison.OrdinalIgnoreCase);
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Checks if a correction exists for the given OCR text.
    /// </summary>
    public bool HasCorrection(string ocrText, string supermarket = "")
    {
        var key = MakeKey(ocrText, supermarket);
        return _corrections.ContainsKey(key);
    }
    
    /// <summary>
    /// Gets the corrected version of OCR text, or the original if no correction exists.
    /// </summary>
    public string GetCorrection(string ocrText, string supermarket = "")
    {
        var key = MakeKey(ocrText, supermarket);
        return _corrections.TryGetValue(key, out var entry) ? entry.CorrectedText : ocrText;
    }
    
    /// <summary>
    /// Gets all corrections for a specific supermarket.
    /// </summary>
    public IEnumerable<CorrectionEntry> GetCorrectionsForSupermarket(string supermarket)
    {
        var market = supermarket.ToUpperInvariant();
        return _corrections.Values
            .Where(e => string.Equals(e.Supermarket, market, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Supermarket, "ALL", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Count);
    }
    
    /// <summary>
    /// Clears all corrections and resets the store.
    /// </summary>
    public void Clear()
    {
        _corrections.Clear();
        Save();
    }
    
    /// <summary>
    /// Exports all learned data for analytics or backup.
    /// </summary>
    public LearnedDataExport ExportData()
    {
        return new LearnedDataExport
        {
            TotalCorrections = _corrections.Count,
            Corrections = _corrections.Values.ToList(),
            ExportedAt = DateTime.UtcNow
        };
    }
    
    // ── Private helpers ──────────────────────────────────────────────────────
    
    private string MakeKey(string ocrText, string supermarket)
    {
        return $"{supermarket.ToUpperInvariant()}::{ocrText.Trim().ToUpperInvariant()}";
    }
    
    private void Load()
    {
        try
        {
            if (!File.Exists(_storagePath))
                return;
                
            lock (_fileLock)
            {
                var json = File.ReadAllText(_storagePath);
                var entries = JsonSerializer.Deserialize<List<CorrectionEntry>>(json) 
                    ?? new List<CorrectionEntry>();
                    
                foreach (var entry in entries)
                {
                    var key = MakeKey(entry.OcrText, entry.Supermarket);
                    _corrections.TryAdd(key, entry);
                }
            }
        }
        catch (Exception ex)
        {
            // If loading fails, start fresh (don't crash the app)
            System.Diagnostics.Debug.WriteLine($"CorrectionStore.Load failed: {ex.Message}");
        }
    }
    
    private void Save()
    {
        try
        {
            lock (_fileLock)
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                    
                var entries = _corrections.Values
                    .OrderByDescending(e => e.Count)
                    .Take(MaxCorrections)
                    .ToList();
                    
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
                
                File.WriteAllText(_storagePath, json);
            }
        }
        catch (Exception ex)
        {
            // If saving fails, log but don't crash
            System.Diagnostics.Debug.WriteLine($"CorrectionStore.Save failed: {ex.Message}");
        }
    }
}

/// <summary>Represents a single correction entry.</summary>
public class CorrectionEntry
{
    /// <summary>The text that OCR incorrectly extracted.</summary>
    public string OcrText { get; set; } = string.Empty;
    
    /// <summary>The correct text as confirmed by user.</summary>
    public string CorrectedText { get; set; } = string.Empty;
    
    /// <summary>The supermarket where this correction applies (or "ALL").</summary>
    public string Supermarket { get; set; } = "ALL";
    
    /// <summary>Number of times this correction has been made.</summary>
    public int Count { get; set; }
    
    /// <summary>When this correction was first recorded.</summary>
    public DateTime FirstSeen { get; set; }
    
    /// <summary>When this correction was last updated.</summary>
    public DateTime LastSeen { get; set; }
}

/// <summary>Export structure for learned data.</summary>
public class LearnedDataExport
{
    public int TotalCorrections { get; set; }
    public List<CorrectionEntry> Corrections { get; set; } = new();
    public DateTime ExportedAt { get; set; }
}
