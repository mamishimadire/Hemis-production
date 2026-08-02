using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers;

[Authorize]
public class WorkingPaperController : Controller
{
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Download()
    {
        using var wb = new XLWorkbook();
        BuildWorkingPaperSheet(wb);
        BuildEmptySheet(wb, "Sheet1");
        BuildSqlvalpacSheet(wb);
        BuildEmptySheet(wb, "Integrity Checks");
        BuildEmptySheet(wb, "Qual Sample");
        BuildEmptySheet(wb, "Creg Sample");
        BuildEmptySheet(wb, "Cesm Sample");
        BuildEmptySheet(wb, "Crse Sample");
        BuildEmptySheet(wb, "Cred Sample");
        BuildEmptySheet(wb, "Stud Sample");
        BuildEmptySheet(wb, "Grad Sample");
        BuildEmptySheet(wb, "Nsfas Sample");
        BuildEmptySheet(wb, "M&D Sample");
        BuildEmptySheet(wb, "Foundation Sample");
        BuildEmptySheet(wb, "Ften Sample");
        BuildEmptySheet(wb, "Staff Sample");
        BuildEmptySheet(wb, "Recon");
        BuildEmptySheet(wb, "Fatal Errors");
        BuildEmptySheet(wb, "Census Date");
        BuildEmptySheet(wb, "Course Code");
        BuildEmptySheet(wb, "Deceased Students");

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "TUT_HEMIS_Working_Paper.xlsx");
    }

    private static void BuildWorkingPaperSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("Working_Paper");
        ws.SheetView.FreezeRows(1);

        var purple = XLColor.FromHtml("#5C2D91");
        var lightGrey = XLColor.FromHtml("#F5F5F5");
        var white = XLColor.White;
        var black = XLColor.Black;

        // ── Note row ─────────────────────────────────────────────────────────
        var noteCell = ws.Cell("B2");
        noteCell.Value = "Please note that this working paper should be used as a guide only and may be amended at any time, as it was developed for guidance purposes.";
        noteCell.Style.Font.Bold = true;
        noteCell.Style.Font.FontSize = 12;
        ws.Range("B2:F2").Merge();

        // ── Section header helper ─────────────────────────────────────────────
        void SectionHeader(int row, string title)
        {
            var r = ws.Range(row, 1, row, 6);
            r.Merge();
            r.FirstCell().Value = title;
            r.Style.Fill.BackgroundColor = purple;
            r.Style.Font.FontColor = white;
            r.Style.Font.Bold = true;
            r.Style.Font.FontSize = 11;
            r.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }

        void LabelValue(int row, string label, string value = "")
        {
            ws.Cell(row, 2).Value = label;
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 3).Value = value;
            if (row % 2 == 0)
                ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = lightGrey;
        }

        void LabelValuePair(int row, string label1, string label2)
        {
            ws.Cell(row, 2).Value = label1;
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 4).Value = label2;
            ws.Cell(row, 4).Style.Font.Bold = true;
        }

        // ── Engagement Information ────────────────────────────────────────────
        SectionHeader(7, "Engagement Information");
        LabelValue(9, "Parent Name");
        LabelValue(10, "Client Name");
        LabelValue(11, "Engagement Name");
        LabelValue(12, "System");
        LabelValue(13, "Financial Year Start");
        LabelValue(14, "Financial Year End");
        LabelValue(15, "Period Reviewed Start", "N/A");
        LabelValue(16, "Period Reviewed End", "N/A");
        LabelValue(17, "Report Signing Date");
        LabelValue(18, "Office", "GAUTENG");
        LabelValue(19, "Reporting Currency", "N/A");

        // ── Working Paper ─────────────────────────────────────────────────────
        SectionHeader(21, "Working Paper");
        LabelValuePair(23, "Performed By", "Date");
        LabelValuePair(24, "Final Review BY", "Date");
        LabelValuePair(25, "Report Date", "Date");
        LabelValue(26, "Analysis", "Data Analysis: Staff and Student Files");
        LabelValue(27, "Script");

        // ── Overall Audit Objective ────────────────────────────────────────────
        SectionHeader(29, "Overall Audit Objective");
        ws.Cell(31, 2).Value = "CAAT";
        ws.Cell(31, 2).Style.Font.Bold = true;
        ws.Cell(31, 3).Value = "Description";
        ws.Cell(31, 3).Style.Font.Bold = true;
        ws.Range(31, 1, 31, 6).Style.Fill.BackgroundColor = lightGrey;
        ws.Range(31, 1, 31, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(31, 1, 31, 6).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.Cell(32, 2).Value = "1";
        ws.Cell(32, 2).Style.Font.Bold = true;
        ws.Cell(32, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(32, 3).Value = "Perform analysis of the 2020 HEMIS directives on the TUT STAFF and STUDENT DATA by following the steps in the \"Analysis Steps\" section.";
        ws.Cell(32, 3).Style.Alignment.WrapText = true;
        ws.Range(32, 3, 32, 6).Merge();
        ws.Range(32, 1, 32, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(32, 1, 32, 6).Style.Border.InsideBorder = XLBorderStyleValues.Hair;

        // ── Analysis Steps ────────────────────────────────────────────────────
        SectionHeader(34, "Analysis Steps");
        ws.Range("34:34").Style.Fill.BackgroundColor = purple;

        var steps = new[]
        {
            "Import data source H16QUAL into ACL",
            "Import data source H16CRSE into ACL",
            "Import data source H16CESM into ACL",
            "Import data source H16CRED into ACL",
            "Import data source H16STUD into ACL",
            "Import data source H16CREG into ACL",
            "Import data source H16PROF into ACL",
            "Import data source dbo_QUAL into ACL",
            "Import data source dbo_CRSE into ACL",
            "Import data source dbo_CESM into ACL",
            "Import data source dbo_CRED into ACL",
            "Import data source dbo_STUD into ACL",
            "Import data source dbo_CREG into ACL",
            "Import data source dbo_PROF into ACL",
            "Import data source dbo_QUAL_VALIDATION_DETAIL into ACL",
            "Import data source dbo_CRSE_VALIDATION_DETAIL into ACL",
            "Import data source dbo_CESM_VALIDATION_DETAIL into ACL",
            "Import data source dbo_CRED_VALIDATION_DETAIL into ACL",
            "Import data source dbo_STUD_VALIDATION_DETAIL into ACL",
            "Import data source dbo_CREG_VALIDATION_DETAIL into ACL",
            "Import data source dbo_PROF_VALIDATION_DETAIL into ACL",
            "Import data source deceased.xlsx into ACL",
            "Import data source 2024 CENSUS DATES.xlsx into ACL",
            "Import data source Census Date list - Final Report.docx into ACL",
            "Import data source MT-audit-prod-CRSE.xlsx into ACL",
            "Import data source MT-audit-prod-QUAL.xlsx into ACL",
            "Import data source MT-Audit-prod-std.xlsx into ACL",
            "Import data source Foundation_Sample_Moira.FIL into ACL",
            "Perform data integrity checks by applying rules #1-9 outlined in Rules Applied (Rule Table)",
            "Prepare the data for analysis by applying rules #10 outlined in Rules Applied (Rule Table)",
            "Select samples by applying rules #11-22 outlined in Rules Applied (Rule Table)",
            "Reconcile the data by applying rules #23-26 outlined in Rules Applied (Rule Table)",
            "Check for Fatal Errors in the data by applying rules #27-33 outlined in Rules Applied (Rule Table)",
            "Census dates check by applying rules #34 outlined in Rules Applied (Rule Table)",
            "Course coding check by applying rules #35 outlined in Rules Applied (Rule Table)",
            "Deceased student check by applying rules #36 outlined in Rules Applied (Rule Table)",
            "Report on the exceptions."
        };

        for (int i = 0; i < steps.Length; i++)
        {
            int row = 36 + i;
            ws.Cell(row, 2).Value = i + 1;
            ws.Cell(row, 3).Value = steps[i];
            ws.Range(row, 3, row, 6).Merge();
            if ((i + 1) % 2 == 0)
                ws.Range(row, 1, row, 6).Style.Fill.BackgroundColor = lightGrey;
        }

        // ── Rules Applied ─────────────────────────────────────────────────────
        int rulesStart = 74;
        SectionHeader(rulesStart, "Rules Applied (Rule Table)");

        int hdrRow = rulesStart + 2;
        ws.Cell(hdrRow, 2).Value = "Rules";
        ws.Cell(hdrRow, 3).Value = "Rules / Calculation Rule";
        ws.Cell(hdrRow, 4).Value = "Rule";
        ws.Cell(hdrRow, 5).Value = "Note";
        ws.Cell(hdrRow, 6).Value = "Script";
        ws.Range(hdrRow, 1, hdrRow, 6).Style.Font.Bold = true;
        ws.Range(hdrRow, 1, hdrRow, 6).Style.Fill.BackgroundColor = lightGrey;

        var rules = new[]
        {
            (1,  "Data integrity checks",
                 "The below filter was applied on dbo_QUAL to check if are there any qualifications with no qualification type.\n\nISBLANK(005)",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (2,  "Data integrity checks",
                 "The below filter was applied on dbo_QUAL to check if are there any qualifications with no approval status.\n\nISBLANK(004)",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (3,  "Data integrity checks",
                 "Checked for duplicates on dbo_QUAL using the 001 field.",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (4,  "Data integrity checks",
                 "Checked for duplicates on dbo_CRSE using the 030 field.",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (5,  "Data integrity checks",
                 "The below filter was applied on dbo_STUD to check if are there any students with invalid student numbers.\n\nNOT MAP(007, \"9999999\")",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (6,  "Data integrity checks",
                 "The below filter was applied on dbo_STUD to check if are there any students with no foundation indicator.\n\nISBLANK(106)",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (7,  "Data integrity checks",
                 "The below filter was applied on dbo_STUD and dbo_QUAL to check if are there any students linked to either no or invalid qualifications.\n\nISBLANK(dbo_STUD.001) OR dbo_STUD.001 <> dbo_QUAL.001",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (8,  "Data integrity checks",
                 "The below filter was applied on dbo_CRSE and dbo_CREG to check if are there any students enrolled for invalid courses.\n\nISBLANK(dbo_CREG.030) OR dbo_CREG.030 <> dbo_CRSE.030",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (9,  "Data integrity checks",
                 "The below filter was applied on dbo_STUD and dbo_CREG to check if are there any student course registration for ghost students.\n\nISBLANK(dbo_CREG.007) OR dbo_CREG.007 <> dbo_STUD.007",
                 "No exceptions were identified", "Refer to Integrity Checks"),
            (10, "Joining Rules",
                 "The tables were joined on the following fields:\n1. dbo_CREG Key field(s): 030, 001+030\n2. dbo_CRSE Key field(s): 030\n3. dbo_STUD Key field(s): 007, 007+001\n4. dbo_QUAL Key field(s): 001\n5. dbo_CESM Key field(s): 001\n6. dbo_CRED Key field(s): 030, 001+030\n7. Census_dates Key field(s): BLOCK_CODE\n8. Census_dates_design Key field(s): BLOCK_CODE\n9. 2024H16STUD Key field(s): STUDNUM, STUDNUM+QUALCODE\n10. Prod_STUD Key field(s): IAGSTNO, IAGSTNO+IAGQUAL\n11. Prod_QUAL Key field(s): IAIQUAL\n12. 2024H16QUAL Key field(s): QUALCODE\n13. Prod_CRSE Key field(s): IALSUBJ\n14. 2024H16CRSE Key field(s): CRSECODE\n15. Employee_file Key field(s): Personnel_Number\n16. dbo_PROF Key field(s): 037\n17. Deceased_Students Key field(s): STUDENT_NUMBER",
                 "N/A", "Refer to Integrity Checks"),
            (11, "Sample Selection",
                 "* From the dbo_QUAL table, selected 40 undergraduate approved qualifications where the approval indicator is A (004 = \"A\") and the qualification type is not postgraduate (NOT MATCH(dbo_QUAL_05, \"07\",\"27\",\"28\",\"49\",\"72\",\"73\",\"08\",\"30\",\"50\",\"74\",\"75\") AND NOT MAP(dbo_QUAL_01,\"M?????\")).\n\n* From the dbo_QUAL table, selected 5 postgraduate approved qualifications where the approval indicator is A (004 = \"A\") and the qualification type is postgraduate (MATCH(dbo_QUAL_05,\"07\",\"27\",\"28\",\"49\",\"72\",\"73\",\"08\",\"30\",\"50\",\"74\",\"75\")).",
                 "Qualification File sample", "Refer to Qual Sample"),
            (12, "Sample Selection",
                 "* From the dbo_CREG table, selected 40 courses",
                 "Course Registration File sample", "Refer to Creg Sample"),
            (13, "Sample Selection",
                 "* From the dbo_CESM table, selected 45 qualifications with a major field CESM (006 <> \"ZZZZZZ\") and there is at least one student registered for the qualification (dbo_QUAL.001 = dbo_STUD.001)",
                 "CESM File sample", "Refer to Cesm Sample"),
            (14, "Sample Selection",
                 "* From the dbo_CRSE table, selected 55 approved courses where the approval indicator is A (031 = \"A\") and there is at least one student registered for the course (dbo_CRSE.030 = dbo_CREG.030)",
                 "Course File sample", "Refer to Crse Sample"),
            (15, "Sample Selection",
                 "* From the dbo_CRED table, selected 40 courses for an approved qualification where the qualification approval indicator is A (004 = \"A\") with at least one student registered for the course (dbo_CREG.001 = dbo_CRED.001 AND dbo_CREG.030 = dbo_CRED.030).",
                 "Credit File sample", "Refer to Cred Sample"),
            (16, "Sample Selection",
                 "* From the dbo_STUD table, selected 10 students where the qualification fulfilled indicator is N (025 = \"N\"), the foundation course indicator is Y (091 = \"Y\").\n\n* From the dbo_STUD table, selected 3 students where the qualification fulfilled indicator is N (025 = \"N\"), the foundation course indicator is Y (091 = \"Y\") and the distance learning indicator is D(024 = \"D\").\n\n* From the dbo_STUD table, selected 47 students where the qualification fulfilled indicator is N (025 = \"N\"), the foundation course indicator is not Y (091 <> \"Y\") and the distance learning indicator is D(024<>\"D\").",
                 "Student File sample", "Refer to Stud Sample"),
            (17, "Sample Selection",
                 "* From the dbo_STUD table, selected 40 graduate students where the qualification fulfilled indicator is F (025 = \"F\").",
                 "Graduates File sample", "Refer to Grad Sample"),
            (18, "Sample Selection",
                 "* From the dbo_STUD table, selected 5 NSFAS students where the NSFAS funded indicator is NS (019 = \"NS\"), and the foundation course indicator is Y (091 = \"Y\").\n\n* From the dbo_STUD table, selected 3 NSFAS students where the NSFAS funded indicator is NS (019 = \"NS\"), and the foundation course indicator is Y (091 = \"Y\") and the distance learning indicator is D(024=\"D\").\n\n* From the dbo_STUD table, selected 32 NSFAS students where the NSFAS funded indicator is NS (019 = \"NS\"), and the foundation course indicator is not Y (091 <> \"Y\") and the distance learning indicator is not D(024<>\"D\").",
                 "NSFAS File sample", "Refer to Nsfas Sample"),
            (19, "Sample Selection",
                 "* From the dbo_STUD table, selected 10 Masters and PhD students where the qualification fulfilled indicator is F (025 = \"F\") and the qualification type is masters and Doctoral (MATCH(dbo_QUAL.005,\"07\",\"27\",\"28\",\"49\",\"72\",\"73\",\"08\",\"30\",\"50\",\"74\",\"75\")).",
                 "Masters and Doctoral File sample", "Refer to M&D Sample"),
            (20, "Sample Selection",
                 "* From the dbo_STUD table, selected 10 foundation students where the Foundation Course and Student indicator is \"Y\" (_91 = \"Y\" AND _06 = \"Y\")",
                 "Foundation File sample", "Refer to Foundation Sample"),
            (21, "Sample Selection",
                 "* From the dbo_STUD table, selected 40 first time entering  students using the below criteria\n\n010 = \"F\"",
                 "First Time Entering sample", "Refer to Ften Sample"),
            (22, "Sample Selection",
                 "* From the dbo_PROF table, selected 30 staff with using the below criteria\n\n_41 = \"PE\" AND _39 = \"01\"\n\nAND\n\n_41 = \"PE\" AND _39 <> \"01\"\n\nAND\n\n_41 <> \"PE\" AND _39 <> \"01\"",
                 "Staff sample", "Refer to Staff Sample"),
            (23, "Reconcile datasets",
                 "The below filters was applied on dbo_STUD\n\nISBLANK(_2024H16STUD.STUDNUM) OR _08 <> _2024H16STUD.SAIDNUM OR _01 <> _2024H16STUD.QUALCODE OR ISBLANK(Prod_STUD.IAGSTNO) OR _08 <> Prod_STUD.IADIDNO OR _01 <> Prod_STUD.IAGQUAL\n\nISBLANK(_2024H16STUD.STUDNUM) OR ISBLANK(Prod_STUD.IAGSTNO)",
                 "No exceptions were identified", "Refer to Recon"),
            (24, "Reconcile datasets",
                 "The below filter was applied on dbo_QUAL\n\n(ISBLANK(_2024H16QUAL.QUALCODE) OR ISBLANK(Prod_Qual.IAIQUAL)) AND _01 = dbo_STUD_01 AND _04 <> \"N\"",
                 "No exceptions were identified", "Refer to Recon"),
            (25, "Reconcile datasets",
                 "The below filter was applied on dbo_CRSE\n\nNOT MATCH(_30, _2024H16CRSE.CRSECODE, Prod_Course.IALSUBJ)",
                 "No exceptions were identified", "Refer to Recon"),
            (26, "Reconcile datasets",
                 "The below filters was applied on dbo_PROF\n\nPersonnel_Number <> dbo_PROF_37\n\nPersonnel_Number = dbo_PROF_37 AND PERMANENT_OR_TEMP <> SUBS(dbo_PROF_41, 1,1)\n\nPersonnel_Number = dbo_PROF_37 AND GND <> dbo_PROF_12\n\nPersonnel_Number = dbo_PROF_37 AND c_Race <> dbo_PROF_14\n\nPersonnel_Number = dbo_PROF_37 AND BIRTH_DATE <> CTOD(ALLT(STR(dbo_PROF_11, 10)), \"YYYYMMDD\")",
                 "No exceptions were identified", "Refer to Recon"),
            (27, "Fatal Errors",
                 "The below filter was applied on dbo_CESM_VALIDATION_DETAIL\n\nALLT(Error_Type) = \"Fatal\"",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (28, "Fatal Errors",
                 "The below filter was applied on dbo_CRED_VALIDATION_DETAIL\n\nALLT(Error_Type) = \"Fatal\" AND NOT MATCH(Error, \"02202\", \"02301\", \"02302\", \"00708\", \"07201\", \"01501\")",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (29, "Fatal Errors",
                 "The below filter was applied on dbo_CREG_VALIDATION_DETAIL\n\nError = \"00708\"\n\nA sample of 25 was taken from the 103 errors",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (30, "Fatal Errors",
                 "The below filter was applied on dbo_PROF_VALIDATION_DETAIL\n\nALLT(Error_Type) = \"Fatal\" AND NOT MATCH(Error, \"02202\", \"02301\", \"02302\", \"00708\", \"07201\", \"01501\")",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (31, "Fatal Errors",
                 "The below filter was applied on dbo_QUAL_VALIDATION_DETAIL\n\nSET FILTER ALLT(Error_Type) = \"Fatal\" AND NOT MATCH(Error, \"02202\", \"02301\", \"02302\", \"00708\", \"07201\", \"01501\")",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (32, "Fatal Errors",
                 "The below filter was applied on dbo_STUD_VALIDATION_DETAIL\n\nALLT(Error_Type) = \"Fatal\" AND NOT MATCH(Error, \"02202\", \"02301\", \"02302\", \"00708\", \"07201\", \"01501\")",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (33, "Fatal Errors",
                 "The below filter was applied on dbo_PROF_VALIDATION_DETAIL\n\nALLT(Error_Type) = \"Fatal\" AND NOT MATCH(Error, \"02202\", \"02301\", \"02302\", \"00708\", \"07201\", \"01501\")",
                 "No exceptions were identified", "Refer to Fatal Errors"),
            (34, "Census Checks",
                 "Calculate fields used to define census dates:\n1. DAYS\na) Calculate the number of days between the First and the Last Class Date.\nb) Save the field as c_Days.\n\n2. DAYS_2\na) Calculate the midpoint by dividing the results in 1. by 2 i.e., c_Days/2.\nb) Save the field as c_Days_2.\n\n3. CENSUS_DATE_PREP\na) Determine the census date based on adding the results in 2. above to the first lecture date i.e., First_Day_Class + c_Days_2.\nb) Save the field as c_Census_Date_Prep.\n\n4. CENSUS_DATE\na) Determine the actual census date based on the results in 3 and if the day falls on a weekend or holiday, the next day or Monday will be the census date.\nb) Save the field as c_ACTUAL_CENSUS_DATE.\n\n5. Verify the defined census date against the computed census date i.e., c_ACTUAL_CENSUS_DATE <> s_CENSUS_DATE.",
                 "No exceptions were identified", "Refer to Census Date"),
            (35, "Coding of courses",
                 "Checked for duplicates on dbo_CRSE using the 030  field.",
                 "No exceptions were identified", "Refer to Course Code"),
            (36, "Deceased students",
                 "The below filter was applied on dbo_STUD\n\n_07 = Deceased_Students.STUDENT_NUMBER",
                 "No exceptions were identified", "Refer to Deceased Students"),
        };

        int dataRow = hdrRow + 1;
        foreach (var (num, category, rule, note, script) in rules)
        {
            ws.Cell(dataRow, 2).Value = num;
            ws.Cell(dataRow, 3).Value = category;
            ws.Cell(dataRow, 4).Value = rule;
            ws.Cell(dataRow, 5).Value = note;
            ws.Cell(dataRow, 6).Value = script;

            ws.Cell(dataRow, 4).Style.Alignment.WrapText = true;
            ws.Cell(dataRow, 4).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            ws.Cell(dataRow, 5).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;

            if (num % 2 == 0)
                ws.Range(dataRow, 1, dataRow, 6).Style.Fill.BackgroundColor = lightGrey;

            dataRow++;
        }

        // Rules border
        ws.Range(hdrRow, 2, dataRow - 1, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(hdrRow, 2, dataRow - 1, 6).Style.Border.InsideBorder = XLBorderStyleValues.Hair;

        // ── Data Source ───────────────────────────────────────────────────────
        int dsStart = dataRow + 2;
        SectionHeader(dsStart, "Data Source");

        int dsHdr = dsStart + 2;
        ws.Cell(dsHdr, 2).Value = "Files";
        ws.Cell(dsHdr, 3).Value = "File Name";
        ws.Cell(dsHdr, 4).Value = "Description";
        ws.Cell(dsHdr, 5).Value = "Note";
        ws.Range(dsHdr, 1, dsHdr, 6).Style.Font.Bold = true;
        ws.Range(dsHdr, 1, dsHdr, 6).Style.Fill.BackgroundColor = lightGrey;

        var dataSources = new[]
        {
            (1,  "H16QUAL",                        "Qualification ASCII files extracted from production."),
            (2,  "H16CRSE",                        "Corse ASCII files extracted from production."),
            (3,  "H16CESM",                        "CESM ASCII files extracted from production."),
            (4,  "H16CRED",                        "Course Credit ASCII files extracted from production."),
            (5,  "H16STUD",                        "Student ASCII files extracted from production."),
            (6,  "H16CREG",                        "Course Registration ASCII files extracted from production."),
            (7,  "H16PROF",                        "Staff ASCII files extracted from production."),
            (8,  "dbo_QUAL",                       "Qualification data from the VALPAC system."),
            (9,  "dbo_CRSE",                       "Course data from the VALPAC system."),
            (10, "dbo_CESM",                       "CESM data from the VALPAC system."),
            (11, "dbo_CRED",                       "Course Credit data from the VALPAC system."),
            (12, "dbo_STUD",                       "Student data from the VALPAC system."),
            (13, "dbo_CREG",                       "Course Registration data from the VALPAC system."),
            (14, "dbo_PROF",                       "Staff data from the VALPAC system."),
            (15, "dbo_QUAL_VALIDATION_DETAIL",     "Qualification validation details from the VALPAC system."),
            (16, "dbo_CRSE_VALIDATION_DETAIL",     "Course validation details from the VALPAC system."),
            (17, "dbo_CESM_VALIDATION_DETAIL",     "CESM validation details from the VALPAC system."),
            (18, "dbo_CRED_VALIDATION_DETAIL",     "Course credit validation details from the VALPAC system."),
            (19, "dbo_STUD_VALIDATION_DETAIL",     "Student validation details from the VALPAC system."),
            (20, "dbo_CREG_VALIDATION_DETAIL",     "Course registration validation details from the VALPAC system."),
            (21, "dbo_PROF_VALIDATION_DETAIL",     "Staff validation details from the VALPAC system."),
            (22, "deceased.xlsx",                  "Course registration data from the VALPAC system."),
            (23, "2024 CENSUS DATES.xlsx",         "Census Date working file for census design"),
            (24, "Census Date list - Final Report.docx", "Census Date file from ITS configuration"),
            (25, "MT-audit-prod-CRSE.xlsx",        "Course master file from ITS"),
            (26, "MT-audit-prod-QUAL.xlsx",        "Qualification master file from ITS"),
            (27, "MT-Audit-prod-std.xlsx",         "Student master file from ITS"),
            (28, "Foundation_Sample_Moira.FIL",    "Foundation students extraction results"),
        };

        int dsRow = dsHdr + 1;
        foreach (var (num, fileName, desc) in dataSources)
        {
            ws.Cell(dsRow, 2).Value = num;
            ws.Cell(dsRow, 3).Value = fileName;
            ws.Cell(dsRow, 3).Style.Font.FontColor = purple;
            ws.Cell(dsRow, 4).Value = desc;
            if (num % 2 == 0)
                ws.Range(dsRow, 1, dsRow, 6).Style.Fill.BackgroundColor = lightGrey;
            dsRow++;
        }
        ws.Range(dsHdr, 2, dsRow - 1, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(dsHdr, 2, dsRow - 1, 6).Style.Border.InsideBorder = XLBorderStyleValues.Hair;

        // ── Analysis Results ──────────────────────────────────────────────────
        int arStart = dsRow + 2;
        SectionHeader(arStart, "Analysis Results");

        int arHdr = arStart + 2;
        ws.Cell(arHdr, 2).Value = "Tabs";
        ws.Cell(arHdr, 3).Value = "Tab Name";
        ws.Cell(arHdr, 4).Value = "Tab Description";
        ws.Cell(arHdr, 5).Value = "Finding Summary / Note";
        ws.Range(arHdr, 1, arHdr, 6).Style.Font.Bold = true;
        ws.Range(arHdr, 1, arHdr, 6).Style.Fill.BackgroundColor = lightGrey;

        int arRow = arHdr + 1;
        ws.Cell(arRow, 2).Value = 1;
        ws.Cell(arRow, 3).Value = "Working Paper";
        ws.Cell(arRow, 4).Value = "Working Paper";
        ws.Cell(arRow, 5).Value = "This worksheet";
        arRow++;

        ws.Cell(arRow, 2).Value = 5;
        ws.Cell(arRow, 3).Value = "SQLVALPAC 00708";
        ws.Cell(arRow, 3).Style.Font.FontColor = XLColor.FromHtml("#0563C1");
        ws.Cell(arRow, 3).Style.Font.Underline = XLFontUnderlineValues.Single;
        ws.Cell(arRow, 4).Value = "Fatal errors \"00708\" appear in the SQLVALPAC CREG validation report.";
        ws.Cell(arRow, 5).Value = "Findings:\nExceptions from the analysis:\nThere were 46 student exceptions identified in the analysis:\n\n* Error codes 00708\n\nConclusion:\nAuditors to further test the evidence of the 15 students selected from the exceptions.";
        ws.Cell(arRow, 5).Style.Alignment.WrapText = true;
        ws.Cell(arRow, 5).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        ws.Row(arRow).Height = 85;
        arRow++;

        ws.Range(arHdr, 2, arRow - 1, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(arHdr, 2, arRow - 1, 6).Style.Border.InsideBorder = XLBorderStyleValues.Hair;

        // ── Conclusion ────────────────────────────────────────────────────────
        int concStart = arRow + 2;
        SectionHeader(concStart, "Conclusion");

        int concRow = concStart + 2;
        ws.Cell(concRow, 2).Value = 1;
        ws.Cell(concRow, 3).Value = "The Data Analytics Team has conducted the analysis, therefore management must provide comments on the finding to be captured in the final report.";
        ws.Range(concRow, 3, concRow, 6).Merge();
        ws.Cell(concRow, 3).Style.Alignment.WrapText = true;
        ws.Range(concRow, 1, concRow, 6).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;

        // Column widths
        ws.Column(1).Width = 4;
        ws.Column(2).Width = 12;
        ws.Column(3).Width = 30;
        ws.Column(4).Width = 70;
        ws.Column(5).Width = 35;
        ws.Column(6).Width = 30;
    }

    private static void BuildSqlvalpacSheet(XLWorkbook wb)
    {
        var ws = wb.Worksheets.Add("SQLVALPAC 00708");
        var purple = XLColor.FromHtml("#5C2D91");
        var white = XLColor.White;

        var hdr = ws.Range("A1:E1");
        hdr.Merge();
        hdr.FirstCell().Value = "SQLVALPAC 00708 — Fatal Error Analysis";
        hdr.Style.Fill.BackgroundColor = purple;
        hdr.Style.Font.FontColor = white;
        hdr.Style.Font.Bold = true;
        hdr.Style.Font.FontSize = 12;

        ws.Cell("A3").Value = "Description";
        ws.Cell("A3").Style.Font.Bold = true;
        ws.Cell("B3").Value = "Fatal errors \"00708\" appear in the SQLVALPAC CREG validation report.";
        ws.Range("B3:E3").Merge();

        ws.Cell("A5").Value = "Findings";
        ws.Cell("A5").Style.Font.Bold = true;
        ws.Cell("B5").Value = "Exceptions from the analysis:\nThere were 46 student exceptions identified in the analysis:\n* Error codes 00708";
        ws.Cell("B5").Style.Alignment.WrapText = true;
        ws.Range("B5:E5").Merge();
        ws.Row(5).Height = 55;

        ws.Cell("A7").Value = "Conclusion";
        ws.Cell("A7").Style.Font.Bold = true;
        ws.Cell("B7").Value = "Auditors to further test the evidence of the 15 students selected from the exceptions.";
        ws.Range("B7:E7").Merge();

        ws.Column("A").Width = 18;
        ws.Column("B").Width = 60;
    }

    private static void BuildEmptySheet(XLWorkbook wb, string name)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell("A1").Value = name;
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A2").Value = "No data — this sheet is populated during the audit engagement.";
        ws.Cell("A2").Style.Font.Italic = true;
        ws.Cell("A2").Style.Font.FontColor = XLColor.FromHtml("#888888");
    }
}
