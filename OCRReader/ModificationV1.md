# Ticket OCR Pipeline

## Goal

Convert the console application to dll, to be used as a library in other projects. Remove PaddleOCRSharp dependency and implement all OCR logic with Tesseract, improving the multi-pass approach and text cleaning to achieve better accuracy on the provided receipt samples.

---

## Architecture

Leave the structure as like the original, but refactor the code to be more modular and maintainable. The main components will be:

## Changes

Remove the program.cs file and create a new class library project called OCRReader. This project will contain all the logic for the OCR process, including image preprocessing, text extraction, and text cleaning. The main class will be called OCRProcessor, which will have methods for each step of the OCR process.
All must be ready to use as a librar and inyect dependency like:
```csharp
builder.RegisterType<OcrBase>().As<IReceiptOCR>();
builder.RegisterType<BronzeOCR>().As<IReceiptOCR>();
```

Additionaly we will have a class in charge to manage the multi-pass approach, called OCRManager, which will handle the different passes and the logic to determine when to stop the process.
This class will also be responsible for managing a image and return the result as Receipt object that will contain all the extracted information using the three models (OcrBase, BronzeOCR, SilverOCR,GoldOCR). 

## Additional proyect

Inclue a testing proyect using xUnit to test all the function, include in that testing proyect the Samples folder from OCRReader proyect, and use the images inside to test it.
The testing proyect will be called OCRReader.Tests, and it will have a reference to the OCRReader proyect, to be able to test all the functions. The tests will be focused on the main functions of the OCR process, such as image preprocessing, text extraction, and text cleaning.

