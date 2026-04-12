using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

namespace OCRReader.Tests
{
    public class OCRPipelineTests
    {
        private readonly string _samplesDir;
        private readonly string _tessDataPath;

        public OCRPipelineTests()
        {
            // Determine samples directory
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _samplesDir = Path.Combine(baseDir, "Samples");
            if (!Directory.Exists(_samplesDir))
            {
                // Fallback to relative path during development
                _samplesDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\Samples"));
            }

            // Determine tessdata directory
            _tessDataPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), @"..\..\..\..\OCRReader\tessdata"));
            if (!Directory.Exists(_tessDataPath))
            {
                _tessDataPath = @"./tessdata";
            }
        }

        [Fact]
        public void BronzeOCR_ProcessTicket_ReturnsItems()
        {
            // Arrange
            var imagePath = GetFirstSampleImage();
            if (imagePath == null) return;

            var bronze = new BronzeOCR(_tessDataPath);

            // Act
            var items = bronze.ProcessTicket(imagePath);

            // Assert
            Assert.NotNull(items);
            // Bronze OCR should return at least some items
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Supermarket));
                Assert.False(string.IsNullOrWhiteSpace(item.Product));
                Assert.True(item.Price >= 0);
            });
        }

        [Fact]
        public void SilverOCR_ProcessTicket_ReturnsItems()
        {
            // Arrange
            var imagePath = GetFirstSampleImage();
            if (imagePath == null) return;

            var silver = new SilverOCR(_tessDataPath);

            // Act
            var items = silver.ProcessTicket(imagePath);

            // Assert
            Assert.NotNull(items);
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Supermarket));
                Assert.False(string.IsNullOrWhiteSpace(item.Product));
                Assert.True(item.Price >= 0);
            });
        }

        [Fact]
        public void GoldOCR_ProcessTicket_ReturnsItems()
        {
            // Arrange
            var imagePath = GetFirstSampleImage();
            if (imagePath == null) return;

            var gold = new GoldOCR(_tessDataPath);

            // Act
            var items = gold.ProcessTicket(imagePath);

            // Assert
            Assert.NotNull(items);
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Supermarket));
                Assert.False(string.IsNullOrWhiteSpace(item.Product));
                Assert.True(item.Price >= 0);
            });
        }

        [Fact]
        public void OCRManager_ProcessReceipt_ReturnsMergedReceipt()
        {
            // Arrange
            var imagePath = GetFirstSampleImage();
            if (imagePath == null) return;

            var bronze = new BronzeOCR(_tessDataPath);
            var silver = new SilverOCR(_tessDataPath);
            var gold = new GoldOCR(_tessDataPath);
            var manager = new OCRManager(bronze, silver, gold);

            // Act
            var receipt = manager.ProcessReceipt(imagePath);

            // Assert
            Assert.NotNull(receipt);
            Assert.False(string.IsNullOrWhiteSpace(receipt.Supermarket));
            Assert.NotNull(receipt.Products);
            Assert.NotEmpty(receipt.Products);
            Assert.All(receipt.Products, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Name));
                Assert.False(string.IsNullOrWhiteSpace(p.Price));
            });
        }

        [Fact]
        public void OCRManager_WithDefaultConstructor_ProcessesReceipt()
        {
            // Arrange
            var imagePath = GetFirstSampleImage();
            if (imagePath == null) return;

            var manager = new OCRManager();

            // Act
            var receipt = manager.ProcessReceipt(imagePath);

            // Assert
            Assert.NotNull(receipt);
            Assert.False(string.IsNullOrWhiteSpace(receipt.Supermarket));
        }

        [Fact]
        public void OCRManager_InvalidImagePath_ThrowsFileNotFoundException()
        {
            // Arrange
            var manager = new OCRManager();

            // Act & Assert
            Assert.Throws<FileNotFoundException>(() => manager.ProcessReceipt("nonexistent.jpg"));
        }

        [Fact]
        public void OCRManager_NullImagePath_ThrowsArgumentException()
        {
            // Arrange
            var manager = new OCRManager();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => manager.ProcessReceipt(null!));
        }

        [Fact]
        public void MergeOCRResult_MergesMultipleModels()
        {
            // Arrange
            var bronzeItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "MILK", 1.50m),
                new("TEST_MARKET", "BREAD", 2.00m)
            };
            var silverItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "MILK", 1.50m),
                new("TEST_MARKET", "BREAD", 2.00m),
                new("TEST_MARKET", "BUTTER", 3.00m)
            };
            var goldItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "MILK", 1.50m),
                new("TEST_MARKET", "BREAD", 2.00m),
                new("TEST_MARKET", "BUTTER", 3.00m),
                new("TEST_MARKET", "EGGS", 2.50m)
            };

            // Act
            var receipt = MergeOCRResult.MergeOCRModels(bronzeItems, silverItems, goldItems);

            // Assert
            Assert.NotNull(receipt);
            Assert.Equal("TEST_MARKET", receipt.Supermarket);
            Assert.NotNull(receipt.Products);
            Assert.True(receipt.Products.Count > 0);
        }

        [Fact]
        public void MergeOCRResult_InvalidProductName_Excluded()
        {
            // Arrange
            var bronzeItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "12345", 1.00m),  // Invalid: only numbers
                new("TEST_MARKET", "VALID PRODUCT", 2.00m)
            };
            var silverItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "12345", 1.00m),
                new("TEST_MARKET", "VALID PRODUCT", 2.00m)
            };
            var goldItems = new List<ReceiptItem>
            {
                new("TEST_MARKET", "12345", 1.00m),
                new("TEST_MARKET", "VALID PRODUCT", 2.00m)
            };

            // Act
            var receipt = MergeOCRResult.MergeOCRModels(bronzeItems, silverItems, goldItems);

            // Assert
            Assert.All(receipt.Products, p =>
            {
                Assert.Matches(@"[a-zA-ZáéíóúñÁÉÍÓÚÑüÜ]", p.Name);
            });
        }

        [Theory]
        [InlineData("Receipt1.jpeg")]
        [InlineData("Receipt3.jpeg")]
        [InlineData("Receipt4.jpeg")]
        public void AllSamples_ProcessSuccessfully(string fileName)
        {
            // Arrange
            var imagePath = Path.Combine(_samplesDir, fileName);
            if (!File.Exists(imagePath)) return;

            var manager = new OCRManager(
                new BronzeOCR(_tessDataPath),
                new SilverOCR(_tessDataPath),
                new GoldOCR(_tessDataPath)
            );

            // Act
            var receipt = manager.ProcessReceipt(imagePath);

            // Assert
            Assert.NotNull(receipt);
        }

        [Fact]
        public void OcrBase_ExtractSupermarket_DetectsKnownMarket()
        {
            // Arrange & Act - we test through BronzeOCR which uses base class methods
            var bronze = new BronzeOCR(_tessDataPath);
            // We can't directly test ExtractSupermarket as it's protected
            // But we can verify the supermarket is detected in the result
            var firstSample = GetFirstSampleImage();
            if (firstSample != null)
            {
                var items = bronze.ProcessTicket(firstSample);
                if (items.Any())
                {
                    Assert.False(string.IsNullOrWhiteSpace(items[0].Supermarket));
                }
            }
        }

        [Fact]
        public void ReceiptItem_CreatesSuccessfully()
        {
            // Arrange & Act
            var item = new ReceiptItem("MARKET", "PRODUCT", 1.50m);

            // Assert
            Assert.Equal("MARKET", item.Supermarket);
            Assert.Equal("PRODUCT", item.Product);
            Assert.Equal(1.50m, item.Price);
        }

        [Fact]
        public void Product_CreatesSuccessfully()
        {
            // Arrange & Act
            var product = new Product { Name = "MILK", Price = "1.50" };

            // Assert
            Assert.Equal("MILK", product.Name);
            Assert.Equal("1.50", product.Price);
        }

        [Fact]
        public void Receipt_CreatesSuccessfully()
        {
            // Arrange & Act
            var receipt = new Receipt
            {
                Supermarket = "TEST",
                Products = new List<Product> { new Product { Name = "MILK", Price = "1.50" } },
                Total = 1.50m
            };

            // Assert
            Assert.Equal("TEST", receipt.Supermarket);
            Assert.Single(receipt.Products);
            Assert.Equal(1.50m, receipt.Total);
        }

        [Fact]
        public void ReceiptLine_CreatesSuccessfully()
        {
            // Arrange & Act
            var line = new ReceiptLine("PRODUCT", "Milk", 1.50m, 1, 1.50m);

            // Assert
            Assert.Equal("PRODUCT", line.Type);
            Assert.Equal("Milk", line.Description);
            Assert.Equal(1.50m, line.Amount);
            Assert.Equal(1, line.Quantity);
            Assert.Equal(1.50m, line.UnitPrice);
        }

        private string? GetFirstSampleImage()
        {
            if (!Directory.Exists(_samplesDir))
                return null;

            return Directory.GetFiles(_samplesDir, "*.jpeg")
                .Concat(Directory.GetFiles(_samplesDir, "*.jpg"))
                .Concat(Directory.GetFiles(_samplesDir, "*.png"))
                .Distinct()
                .OrderBy(f => f)
                .FirstOrDefault();
        }
    }
}
