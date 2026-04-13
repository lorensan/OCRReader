# GOAL

To be able to integrate the OCRReader into Android application we need to change some libraries to get compatible with mobile infrastructure.

# Modifications

##    The plan

     1. Open the OCRReader project source code
     2. Replace all System.Drawing usage with SkiaSharp equivalents
     3. Update Tesseract wrapper to work with SKBitmap (Tesseract can read any bitmap format)
     4. Add SkiaSharp.NativeAssets.Android if needed
     5. Rebuild the DLL → replace the reference in your MAUI project
     6. Add unit tests to verify the OCR functionality is working correctly with SkiaSharp