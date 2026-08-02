using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HemisAudit.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers;

[Authorize]
public class ClinicalDraftReportController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Download()
    {
        var stream = new MemoryStream();
        BuildDocument(stream);
        stream.Position = 0;
        return File(stream,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "Clinical_Training_Enrollment_AUP_Draft_Report.docx");
    }

    private static void BuildDocument(MemoryStream ms)
    {
        using var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();

        // ── Letterhead ─────────────────────────────────────────────────────────
        WordHelper.AddHeaderTable(body,
            ["The Chief Financial Officer (CFO)", "Tshwane University of Technology", "Private Bag X680", "Pretoria", "0001"],
            ["SNG Grant Thornton", "152, 14th Road", "Noordwyk", "Midrand, 1687", "T +27 (0) 86 117 6782"]);

        body.Append(WordHelper.Empty(8));

        // ── Title ──────────────────────────────────────────────────────────────
        body.Append(WordHelper.WPara(
            "Agreed-upon Procedures Report on Clinical Training Enrollment Audit for the academic year ended 31 December 2025.",
            bold: true, italic: true, color: WordHelper.Purple, sizePt: 10, afterPt: 8));

        // ── Disclaimer note ────────────────────────────────────────────────────
        body.Append(WordHelper.WPara(
            "Please note that this Draft Report should be used as a guide only and may be amended at any time, as it was developed for guidance purposes.",
            italic: true, color: WordHelper.Purple, afterPt: 10));

        // ── Sections ───────────────────────────────────────────────────────────
        WordHelper.AddSection(body, "Purpose of this Agreed-Upon Procedure Report and Restriction on Use and Distribution",
            "Our report is solely for the purpose of assisting the Tshwane University of Technology in determining whether the Clinical Enrolment data submitted to the Department of Higher Education and Training (DHET) are compliant with the requirements of the DHET Clinical Training Enrolment policy and may not be suitable for any other purpose. This report is intended solely for the use of the Tshwane University of Technology and the DHET, and should not be used by, or distributed to, any other parties.");

        WordHelper.AddSection(body, "Responsibility of the Engaging Party and the Responsible Party",
            "Tshwane University of Technology has acknowledged that the agreed-upon procedures are appropriate for the purpose of the engagement.\n\nThe Acting DVC: Digital Transformation, as identified by the Tshwane University of Technology, is responsible for the subject matter on which the agreed-upon procedures are performed");

        WordHelper.AddSection(body, "Practitioner’s responsibility",
            "We have conducted the agreed-upon procedures engagement in accordance with the International Standard on Related Services (ISRS) 4400 (Revised), Agreed-Upon Procedures Engagements. An Agreed-upon procedures engagement involves our performing the procedures that have been agreed with the Tshwane University of Technology, and reporting the findings, which are the factual results of the agreed upon procedures performed. We make no representation regarding the appropriateness of the agreed-upon procedures.\n\nThe agreed-upon procedures engagement is not an assurance engagement. Accordingly, we do not express an opinion or an assurance conclusion.\n\nHad we performed additional procedures, other matters might have come to our attention that would have been reported.");

        WordHelper.AddSection(body, "Professional Ethics and Quality Management",
            "We have complied with the ethical requirements in accordance with the International Ethics Standards Board for Accountants’ International Code of Ethics for Professional Accountants (IESBA Code) and in accordance with other ethical requirements applicable to performing agreed-upon procedures engagements in South Africa.\n\nOur Firm applies International Standard on Quality Management 1 (ISQM1), Quality Management for firms that perform Audits or Reviews of Financial Statements, or Other Assurance or Related Services Engagements, which requires the firm to design, implement and operate a system of quality management including policies or procedures regarding compliance with ethical requirements, professional standards and applicable legal and regulatory requirements.");

        body.Append(WordHelper.WPara("Procedures and Findings", bold: true, afterPt: 4));
        body.Append(WordHelper.WPara(
            "We have performed the procedures described below, which were agreed upon with the Tshwane University of Technology management as delegated by The Council of the University in the engagement letter dated 20 May 2026.",
            afterPt: 8));

        // ── Procedures table ───────────────────────────────────────────────────
        var tbl = WordHelper.CreateProcTable();

        tbl.Append(WordHelper.ProcDataRow("1",
            "Obtain from Statutory Reporting: Student and Space in the Strategic Management Support Department of the University, the Clinical Training Student WIL List for 2025 from the University's official student records.",
            ("We have obtained from Statutory Reporting: Student and Space in the Strategic Management Support Department, the Clinical Training Student WIL List for 2025 from the University's official student records.", false, (string?)null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("2",
            "Obtain the information in procedures 2.1 and 2.2 below and perform the procedure in 2.3 as follows:",
            ("We have obtained the information in procedures 2.1 and 2.2 below and performed the procedure in 2.3 as follows:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("2.1",
            "Obtain from Statutory Reporting: Student and Space in the Strategic Management Support Department, the Clinical Training HEMIS Headcounts enrolment data for the 2025 academic year from the University’s official student record system (“HEMIS”).",
            ("We have obtained from Statutory Reporting: Student and Space in the Strategic Management Support Department, the Clinical Training HEMIS Headcounts enrolment data for the 2025 academic year from the University’s official student record system (“HEMIS”).", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("2.2",
            "Obtain from the Statutory Reporting: Student and Space in the Strategic Management Support Department of the University, the screenshots of data queried on the HEMIS database.",
            ("We have obtained from the Statutory Reporting: Student and Space in the Strategic Management Support Department of the University, the screenshots of data queried on the HEMIS database.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("2.3",
            "Agree the number of students per qualification to the HEMIS database and screenshots obtained in procedures 2.1. and 2.2. above",
            ("We have agreed the number of students per qualification of 1 271 to the HEMIS database and screenshots obtained in procedures 2.1. and 2.2. above.", false, null),
            ("See below breakdown of number of students confirmed:", false, null),
            ("Qualification / Number of students:\nPharmacy: 213  |  Nursing: 248  |  Biomedical Technology: 204\nClinical Technology: 170  |  Radiography: 186  |  Biokinetics: 99\nMedical Orthotics & Prosthetics: 151  |  Total: 1 271", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("3",
            "Select a sample of students for each area of study for the 2025 academic year. The sample should only be selected from student criteria that contain curriculum-stated Work Integrated Learning (WIL) periods contained in procedure 1 above.",
            ("We have selected a sample of 5 students for each area of study for the 2025 academic year. The sample was only selected from student criteria that contain curriculum-stated Work Integrated Learning (WIL) periods contained in procedure 1 above.", false, null),
            ("See below breakdown of sample selected (Area of Study / WIL Population / Sample selected):\nPharmacy: 140 / 5  |  Nursing: 233 / 5  |  Biomedical Technology: 90 / 5\nClinical Technology: 122 / 5  |  Radiography: 161 / 5  |  Biokinetics: 96 / 5\nMedical Orthotics & Prosthetics: 30 / 5  |  Total: 872 / 35", false, null)));

        tbl.Append(WordHelper.ProcDataRow("4",
            "Inspect evidence obtained from the Deputy-Vice Chancellor: Teaching, Learning and Technology indicating that students selected in procedure 3 were active students at the University in the 2025 academic year (i.e. inspect logbooks, hour sheets, workbooks, portfolio of evidence, and proof of registration/academic records).",
            ("Inspected evidence obtained from the Deputy-Vice Chancellor: Teaching, Learning and Technology indicating that students selected in procedure 3 were active students at the University in the 2025 academic year (i.e. inspect logbooks, hour sheets, workbooks, portfolio of evidence, and proof of registration/academic records).", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5",
            "Obtain confirmation from the Acting DVC: Digital Transformation stating that the health sciences programme is not offered in partnership with a college or external institution and the University carries full academic and administrative responsibility for the programme",
            ("Obtained confirmation from the Acting DVC: Digital Transformation stating that the health sciences programme is not offered in partnership with a college or external institution and the University carries full academic and administrative responsibility for the programme", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6",
            "The Health Science programmes must be accredited and offer training within health science disciplines.",
            ("Inspected the approved 2025 University Prospectus obtained from the Deputy-Vice Chancellor: Teaching, Learning and Technology and confirmed that the Health Science programmes are accredited and offer training within health science disciplines.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("7",
            "Inspect the approved 2025 University Prospectus obtained from the Deputy-Vice Chancellor: Teaching, Learning and Technology that the curriculum of the health sciences programme includes clinical training which requires students to have access to the facilities, patients, and clinical staff of provincial health care services. (Sample selected in accordance with Procedure 3).",
            ("For the sample selected, inspected the approved 2025 University Prospectus obtained from the Deputy-Vice Chancellor: Teaching, Learning and Technology and found that the curriculum of the health sciences programme included clinical training which required students to have access to the facilities, patients and clinical staff of provincial health care services.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8",
            "For the undergraduate level, inspect the approved 2025 University Prospectus that only the health sciences programmes which offer initial training in a health sciences discipline are included in the schedule.",
            ("For the undergraduate level, inspected the approved 2025 University Prospectus that only the health sciences programmes which offer initial training in a health sciences discipline are included in the schedule.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9",
            "For students enrolled for master’s in medicine and family medicine, inspect their proof of registration that the first year of registration was not 2019 or earlier.",
            ("We noted that there are no students enrolled for master’s in medicine and family medicine therefore the procedure is not applicable.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("10",
            "Agree the head counts enrolment in the DHET reporting template (2025 Head Count Enrolments By Clinical Programme) received from the University to the Clinical Training HEMIS Headcounts enrolment data.",
            ("Agreed the head counts enrolment of 1 271 in the DHET reporting template (2025 Head Count Enrolments By Clinical Programme) received from the University to the Clinical Training HEMIS Headcounts enrolment data of 1 271.", false, null),
            ("", false, null),
            ("No exceptions noted.", true, WordHelper.Purple)));

        body.Append(tbl);
        body.Append(WordHelper.Empty(12));

        // ── Signature ──────────────────────────────────────────────────────────
        body.Append(WordHelper.WPara("_______________________________________________", afterPt: 2));
        body.Append(WordHelper.WPara("SizweNtsalubaGobodo Grant Thornton Inc.", bold: true, color: WordHelper.Purple, afterPt: 0));
        body.Append(WordHelper.WPara("Mamishi", bold: true, color: WordHelper.Purple, afterPt: 2));
        body.Append(WordHelper.WPara("Director", afterPt: 0));
        body.Append(WordHelper.WPara("Registered Auditor", afterPt: 6));
        body.Append(WordHelper.WPara("Date: 20 July 2026", afterPt: 6));
        body.Append(WordHelper.WPara("152 14th Road Noordwyk", afterPt: 0));
        body.Append(WordHelper.WPara("Midrand, 1687", afterPt: 0));

        body.Append(WordHelper.PageSetup());
        main.Document.Append(body);
        main.Document.Save();
    }
}
