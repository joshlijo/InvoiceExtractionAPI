using Azure;
using Azure.AI.FormRecognizer;
using Azure.AI.FormRecognizer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.Annotations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DocumentIntelligence.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentIntelligenceController : ControllerBase
    {
        private readonly FormRecognizerClient _recognizerClient;
        private readonly ILogger<DocumentIntelligenceController> _logger;

        private static readonly string[] AllowedContentTypes = { "application/pdf", "image/jpeg", "image/png" };
        private static readonly Dictionary<string, List<string>> FieldSynonyms = new()
        {
            { "InvoiceId", new() { "InvoiceId", "Invoice Number", "Invoice No", "Bill Number", "Reference Number" } },
            { "InvoiceDate", new() { "InvoiceDate", "Date", "Invoice Date", "Billing Date", "Issue Date" } },
            { "VendorName", new() { "VendorName", "Company Name", "Business Name", "Corporate Name", "Enterprise Name", "VendorAddressRecipient" } },
            { "VendorAddress", new() { "VendorAddress", "Address", "Location", "Business Address", "Office Address" } },
            { "CustomerName", new() { "CustomerName", "Bill To", "Invoice To", "Customer", "Recipient", "BillingAddressRecipient", "ShippingAddressRecipient" } },
            { "CustomerAddress", new() { "CustomerAddress", "Ship To", "Delivery To", "Shipping Address", "Consignee", "BillingAddress", "ShippingAddress" } },
            { "InvoiceTotal", new() { "InvoiceTotal", "Amount", "Total Amount Before Tax", "Invoice Total", "Grand Total" } },
            { "SubTotal", new() { "SubTotal", "Amount Before Tax", "Pre-Tax Total" } },
            { "TotalTax", new() { "TotalTax", "TaxAmount", "Tax Total", "Taxes", "Tax" } },
            { "PurchaseOrder", new() { "PurchaseOrder", "PO Number", "Order Number" } },
            { "CGST", new() { "CGST", "Central GST", "Central Goods and Services Tax", "CGST Amount" } },
            { "SGST", new() { "SGST", "State GST", "State Goods and Services Tax", "SGST Amount" } },
            { "IGST", new() { "IGST", "Integrated GST", "Integrated Goods and Services Tax", "IGST Amount" } },
            { "GSTIN", new() { "GSTIN", "GST TIN No", "GST Identification Number", "Goods and Services Tax Number" } },
            { "Items", new() { "Items", "ProductDetails", "ItemDetails", "Product", "Description", "Service" } },
            { "PAN", new() { "PAN No", "Permanent Account Number" } },
            { "AccountName", new() { "Account Name", "Bank Account Name" } },
            { "BankName", new() { "Bank Name" } },
            { "AccountNumber", new() { "OD A/c No", "Account Number" } },
            { "IFSC", new() { "IFSC Code" } },
            { "AmountInWords", new() { "Amount in words" } },
            { "BillToStateCode", new() { "Bill To State Code" } },
            { "ShipToStateCode", new() { "Ship To State Code" } }
        };

        private const double LowConfidenceThreshold = 50.0;
        private const double HighConfidenceForNumbers = 90.0;
        private const double HighConfidenceForPatterns = 85.0;
        private const double LowConfidence = 25.0;

        public DocumentIntelligenceController(FormRecognizerClient recognizerClient, ILogger<DocumentIntelligenceController> logger)
        {
            _recognizerClient = recognizerClient;
            _logger = logger;
        }

        [HttpPost]
        [Route("analyze")]
        [SwaggerOperation(Summary = "Analyze an uploaded document", Description = "Analyzes an uploaded PDF, JPG, or PNG document and extracts relevant invoice data.")]
        [SwaggerResponse(200, "Document analyzed successfully", typeof(SuccessResponse))]
        [SwaggerResponse(400, "Invalid file uploaded or no file uploaded", typeof(ErrorResponse))]
        [SwaggerResponse(500, "Internal server error", typeof(ErrorResponse))]
        public async Task<IActionResult> Analyze(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    _logger.LogWarning("No file uploaded or file is empty.");
                    return BadRequest(new ErrorResponse { Error = "Please select a file.", ErrorCode = "FileEmpty" });
                }

                if (!AllowedContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Unsupported file type uploaded: {ContentType}", file.ContentType);
                    return BadRequest(new ErrorResponse { Error = "Please upload a PDF, JPG, or PNG file.", ErrorCode = "InvalidFileType" });
                }

                using var stream = file.OpenReadStream();
                var invoiceData = await AnalyzeInvoiceAsync(stream);

                var jsonResult = JsonSerializer.Serialize(invoiceData, new JsonSerializerOptions { WriteIndented = true });
                _logger.LogInformation("Invoice Data: {jsonResult}", jsonResult);

                return Ok(new SuccessResponse { InvoiceData = invoiceData });
            }
            catch (RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure Form Recognizer error: {Message}", ex.Message);
                return StatusCode(500, new ErrorResponse { Error = "Internal server error: Azure Form Recognizer error.", ErrorCode = "AzureError" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing invoice for file {fileName}", file?.FileName);
                return StatusCode(500, new ErrorResponse { Error = "Internal server error.", ErrorCode = "UnknownError" });
            }
        }

        private async Task<Dictionary<string, object>> AnalyzeInvoiceAsync(Stream file)
        {
            var options = new RecognizeInvoicesOptions { Locale = "en-US" };
            RecognizedFormCollection invoices = await _recognizerClient.StartRecognizeInvoicesAsync(file, options).WaitForCompletionAsync();

            var invoiceData = InitializeInvoiceData();

            foreach (var invoice in invoices)
            {
                ExtractInvoiceFields(invoice, invoiceData);
                ExtractTableData(invoice, invoiceData);
            }

            PostProcessLowConfidenceFields(invoiceData);

            return invoiceData;
        }

        private static Dictionary<string, object> InitializeInvoiceData()
        {
            return new Dictionary<string, object>
            {
                { "InvoiceFields", new Dictionary<string, object>() },
                { "ProductDetails", new List<Dictionary<string, object>>() },
                { "AmountDetails", new Dictionary<string, object>() },
                { "ClientInformation", new Dictionary<string, object>() },
                { "PaymentDetails", new Dictionary<string, object>() },
                { "TableDetails", new List<Dictionary<string, object>>() }
            };
        }

        private void ExtractInvoiceFields(RecognizedForm invoice, Dictionary<string, object> invoiceData)
        {
            var invoiceFields = (Dictionary<string, object>)invoiceData["InvoiceFields"];
            var productDetailsList = (List<Dictionary<string, object>>)invoiceData["ProductDetails"];
            var amountDetails = (Dictionary<string, object>)invoiceData["AmountDetails"];
            var clientInformation = (Dictionary<string, object>)invoiceData["ClientInformation"];
            var paymentDetails = (Dictionary<string, object>)invoiceData["PaymentDetails"];

            var productDetails = new Dictionary<string, object>();

            foreach (var kvp in FieldSynonyms)
            {
                var targetDict = GetTargetDictionary(kvp.Key, invoiceFields, amountDetails, clientInformation, paymentDetails, productDetails);
                AddFieldIfExists(kvp.Value, kvp.Key, targetDict, invoice);
            }

            if (productDetails.Count > 0)
            {
                productDetailsList.Add(productDetails);
            }
        }

        private void ExtractTableData(RecognizedForm invoice, Dictionary<string, object> invoiceData)
        {
            var tableDetails = (List<Dictionary<string, object>>)invoiceData["TableDetails"];
            foreach (var table in invoice.Pages.SelectMany(p => p.Tables))
            {
                var tableData = new List<List<string>>();
                foreach (var cell in table.Cells)
                {
                    EnsureTableDataCapacity(tableData, cell.RowIndex, cell.ColumnIndex);
                    tableData[cell.RowIndex][cell.ColumnIndex] = cell.Text;
                }
                tableDetails.Add(new Dictionary<string, object> { { "Table", tableData } });
            }
        }

        private static Dictionary<string, object> GetTargetDictionary(string key, Dictionary<string, object> invoiceFields, Dictionary<string, object> amountDetails, Dictionary<string, object> clientInformation, Dictionary<string, object> paymentDetails, Dictionary<string, object> productDetails)
        {
            return key switch
            {
                "Items" or "ProductDetails" or "ItemDetails" or "Product" or "Description" => productDetails,
                "InvoiceTotal" or "SubTotal" or "TotalTax" or "PurchaseOrder" => amountDetails,
                "CustomerName" or "CustomerAddress" or "CustomerAddressRecipient" => clientInformation,
                "AccountName" or "BankName" or "AccountNumber" or "IFSC" => paymentDetails,
                _ => invoiceFields
            };
        }

        private static void AddFieldIfExists(IEnumerable<string> fieldNames, string originalFieldName, Dictionary<string, object> targetDict, RecognizedForm invoice)
        {
            foreach (var fieldName in fieldNames)
            {
                if (invoice.Fields.TryGetValue(fieldName, out var field) && field?.ValueData != null)
                {
                    string value = ExtractFieldValue(field);
                    if (value != null)
                    {
                        double confidence = field.Confidence * 100;
                        AddFieldToDictionary(originalFieldName, targetDict, value, confidence);
                    }
                }
            }
        }

        private static string ExtractFieldValue(FormField field)
        {
            try
            {
                return field.Value.ValueType switch
                {
                    FieldValueType.String => field.Value.AsString(),
                    FieldValueType.Date => field.Value.AsDate().ToString("yyyy-MM-dd"),
                    FieldValueType.Float => field.Value.AsFloat().ToString(),
                    _ => field.ValueData.Text
                };
            }
            catch (InvalidOperationException)
            {
                return field.ValueData.Text;
            }
        }

        private static void AddFieldToDictionary(string originalFieldName, Dictionary<string, object> targetDict, string value, double confidence)
        {
            if (!targetDict.ContainsKey(originalFieldName))
            {
                targetDict[originalFieldName] = new List<Dictionary<string, object>>();
            }

            var existingEntries = (List<Dictionary<string, object>>)targetDict[originalFieldName];
            if (!existingEntries.Any(entry => entry["Value"].Equals(value)))
            {
                existingEntries.Add(new Dictionary<string, object>
                {
                    { "Value", value },
                    { "Confidence", $"{confidence:F2}%" }
                });
            }
        }

        private static void EnsureTableDataCapacity(List<List<string>> tableData, int rowIndex, int columnIndex)
        {
            while (tableData.Count <= rowIndex)
            {
                tableData.Add(new List<string>());
            }
            while (tableData[rowIndex].Count <= columnIndex)
            {
                tableData[rowIndex].Add(string.Empty);
            }
        }

        private static void PostProcessLowConfidenceFields(Dictionary<string, object> invoiceData)
        {
            ProcessInvoiceFields(invoiceData);
            ProcessAmountDetails(invoiceData);
        }

        private static void ProcessInvoiceFields(Dictionary<string, object> invoiceData)
        {
            if (invoiceData.TryGetValue("InvoiceFields", out var invoiceFieldsObj) && invoiceFieldsObj is Dictionary<string, object> invoiceFieldsDict)
            {
                foreach (var fieldKey in invoiceFieldsDict.Keys.ToList())
                {
                    if (invoiceFieldsDict[fieldKey] is List<Dictionary<string, object>> fieldList)
                    {
                        foreach (var fieldDict in fieldList)
                        {
                            ReevaluateFieldConfidence(fieldDict, fieldKey);
                        }
                    }
                }
            }
        }

        private static void ProcessAmountDetails(Dictionary<string, object> invoiceData)
        {
            if (invoiceData.TryGetValue("AmountDetails", out var amountDetailsObj) && amountDetailsObj is Dictionary<string, object> amountDetailsDict)
            {
                foreach (var fieldKey in amountDetailsDict.Keys.ToList())
                {
                    if (amountDetailsDict[fieldKey] is List<Dictionary<string, object>> fieldList)
                    {
                        foreach (var fieldDict in fieldList)
                        {
                            ReevaluatePurchaseOrderConfidence(fieldDict, fieldKey);
                        }
                    }
                }
            }
        }

        private static void ReevaluateFieldConfidence(Dictionary<string, object> fieldDict, string fieldKey)
        {
            if (fieldDict.TryGetValue("Confidence", out var confidenceValue))
            {
                if (double.TryParse(confidenceValue.ToString().TrimEnd('%'), out double confidence) && confidence < LowConfidenceThreshold)
                {
                    string extractedValue = fieldDict["Value"].ToString();
                    confidence = ReevaluateConfidence(extractedValue, confidence, fieldKey);
                    fieldDict["Confidence"] = $"{confidence:F2}%";
                }
            }
        }

        private static void ReevaluatePurchaseOrderConfidence(Dictionary<string, object> fieldDict, string fieldKey)
        {
            if (fieldDict.TryGetValue("Confidence", out var confidenceValue))
            {
                if (double.TryParse(confidenceValue.ToString().TrimEnd('%'), out double confidence) && confidence < LowConfidenceThreshold)
                {
                    string extractedValue = fieldDict["Value"].ToString();
                    if (fieldKey == "PurchaseOrder" && Regex.IsMatch(extractedValue, @"^\d{10}$"))
                    {
                        confidence = HighConfidenceForPatterns;
                        fieldDict["Confidence"] = $"{confidence:F2}%";
                    }
                }
            }
        }

        private static double ReevaluateConfidence(string extractedValue, double confidence, string fieldKey)
        {
            if (Regex.IsMatch(extractedValue, @"^\d+(\.\d{1,2})?$"))
            {
                confidence = HighConfidenceForNumbers;
            }
            else if (Regex.IsMatch(extractedValue, @"^[A-Z0-9/-]+$"))
            {
                confidence = HighConfidenceForPatterns;
            }
            else
            {
                confidence = LowConfidence;
            }

            return confidence;
        }
    }

    public class ErrorResponse
    {
        public string Error { get; set; }
        public string ErrorCode { get; set; }
    }

    public class SuccessResponse
    {
        public Dictionary<string, object> InvoiceData { get; set; }
    }
}