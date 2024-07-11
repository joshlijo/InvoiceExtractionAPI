# Invoice Extraction API

This repository contains a .NET Core API for extracting invoice data from uploaded documents using the Azure Form Recognizer service. The API is designed to process PDF, JPG, and PNG files, extracting key invoice fields and returning the data in a structured format.

## Features

- Extracts invoice data including invoice number, date, vendor details, customer details, total amounts, tax details, and more.
- Supports multiple file formats: PDF, JPG, and PNG.
- Utilizes Azure Form Recognizer for robust and accurate data extraction.
- Provides a detailed Swagger documentation for API endpoints.

## Prerequisites

- .NET 6 SDK or later
- Azure account with Form Recognizer resource
- Azure Form Recognizer endpoint and key
