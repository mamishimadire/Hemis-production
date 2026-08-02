using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HemisAudit.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers;

[Authorize]
public class ClinicalEngagementLetterController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Download()
    {
        var stream = new MemoryStream();
        BuildDocument(stream);
        stream.Position = 0;
        return File(stream,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "Clinical_Training_AUP_Engagement_Letter.docx");
    }

    private static void BuildDocument(MemoryStream ms)
    {
        using var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();

        // ── Letterhead ─────────────────────────────────────────────────────────
        WordHelper.AddHeaderTable(body,
            ["The Chief Financial Officer (CFO)", "........ University", "Private Bag X600", "Pretoria", "0001"],
            ["Auditor Firm Name", "Address line 1", "Address line 2", "City", "T [phone number]"]);

        body.Append(WordHelper.Empty(8));

        // ── Date & Subject ─────────────────────────────────────────────────────
        body.Append(WordHelper.WPara("xx May xxxx", afterPt: 4));
        body.Append(WordHelper.WPara(
            "Clinical Training Enrolment – Agreed Upon Procedures for the period ending xx December xxxx",
            bold: true, color: WordHelper.Purple, sizePt: 10, afterPt: 8));

        // ── Disclaimer note ────────────────────────────────────────────────────
        body.Append(WordHelper.WPara(
            "Please note that this Engagement letter should be used as a guide only and may be amended at any time, as it was developed for guidance purposes.",
            italic: true, color: WordHelper.Purple, afterPt: 10));

        body.Append(WordHelper.WPara("Dear Mr [Name]", afterPt: 6));
        body.Append(WordHelper.WPara(
            "We are pleased to confirm our acceptance of the engagement to perform the Agreed-Upon Procedures (AUP) on the Clinical Training Enrolment data submitted to the Department of Higher Education and Training (DHET) by ........ University for the period ending xx December xxxx. The terms of this engagement are as set out below.",
            afterPt: 8));

        // ── Section 1 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("1.", "Background"));
        body.Append(WordHelper.WPara(
            "........ University (the \"University\") submits Clinical Training Enrolment data to the Department of Higher Education and Training (DHET) annually in terms of the DHET Clinical Training Enrolment policy. The DHET requires independent verification of the Clinical Training Enrolment data submitted by the University to ensure compliance with the applicable policy requirements.",
            afterPt: 6));

        // ── Section 2 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("2.", "Scope of the Engagement"));
        body.Append(WordHelper.WPara(
            "We will perform the following agreed-upon procedures as set out in the AUP report in respect of the Clinical Training Enrolment data submitted by the University for the period ending xx December xxxx:",
            afterPt: 4));
        body.Append(WordHelper.IndentPara("Procedure 1:  Obtain the Clinical Training Student WIL List", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 2:  Obtain HEMIS Headcount enrolment data and screenshots, and agree student numbers to HEMIS database", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 3:  Select a sample of students for each area of study", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 4:  Inspect evidence of student activity (logbooks, hour sheets, workbooks, portfolio of evidence, proof of registration)", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 5:  Obtain confirmation regarding health sciences programme partnerships", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 6:  Confirm accreditation of Health Science programmes", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 7:  Inspect University Prospectus for clinical training requirements", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 8:  Inspect Prospectus for undergraduate initial training health science programmes", leftTwips: 360));
        body.Append(WordHelper.IndentPara("Procedure 9:  Inspect proof of registration for master's in medicine and family medicine students", leftTwips: 360, afterPt: 6));

        // ── Section 3 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("3.", "Responsibilities of the Engaging Party"));
        body.Append(WordHelper.WPara(
            "........ University, as the engaging party, has agreed that the procedures set out in Section 2 above are appropriate for the purpose of the engagement. ........ University is responsible for:\n\n" +
            "a) Ensuring that the agreed-upon procedures are appropriate for the purpose of the engagement;\n" +
            "b) Providing access to all relevant records, systems, and personnel as required to perform the agreed-upon procedures;\n" +
            "c) Ensuring the accuracy and completeness of the information provided to us;\n" +
            "d) Providing written representations confirming matters relevant to this engagement;\n" +
            "e) Obtaining any required third-party consents, permissions, or authorisations in relation to the provision of information to us.",
            afterPt: 6));

        // ── Section 4 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("4.", "Responsibilities of the Responsible Party"));
        body.Append(WordHelper.WPara(
            "The Acting DVC: Digital Transformation, as identified by ........ University, is responsible for the subject matter on which the agreed-upon procedures are performed, including the Clinical Training Enrolment data submitted to the DHET.",
            afterPt: 6));

        // ── Section 5 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("5.", "Standards Applied"));
        body.Append(WordHelper.WPara(
            "We will conduct this engagement in accordance with the International Standard on Related Services (ISRS) 4400 (Revised), Agreed-Upon Procedures Engagements. The engagement is not an audit, review, or other assurance engagement, and accordingly, we will not express an opinion or an assurance conclusion on the subject matter.",
            afterPt: 6));

        // ── Section 6 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("6.", "Our Appointment"));
        body.Append(WordHelper.WPara(
            "We have been appointed by ........ University to perform the agreed-upon procedures as described in this engagement letter. Our appointment is subject to the terms and conditions set out herein.",
            afterPt: 6));

        // ── Section 7 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("7.", "Term of Appointment"));
        body.Append(WordHelper.WPara(
            "This engagement commences on the date of acceptance of this letter and concludes upon delivery of our report. The engagement covers the Clinical Training Enrolment data for the period ending xx December xxxx.",
            afterPt: 6));

        // ── Section 8 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("8.", "Independence"));
        body.Append(WordHelper.WPara(
            "ISRS 4400 (Revised) does not require us to be independent. However, we will comply with the ethical requirements in accordance with the International Ethics Standards Board for Accountants' International Code of Ethics for Professional Accountants (IESBA Code) and other ethical requirements applicable to performing agreed-upon procedures engagements in South Africa.",
            afterPt: 6));

        // ── Section 9 ─────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("9.", "Quality Management"));
        body.Append(WordHelper.WPara(
            "Our Firm applies International Standard on Quality Management 1 (ISQM 1), Quality Management for Firms that Perform Audits or Reviews of Financial Statements, or Other Assurance or Related Services Engagements, which requires the firm to design, implement and operate a system of quality management including policies or procedures regarding compliance with ethical requirements, professional standards and applicable legal and regulatory requirements.",
            afterPt: 6));

        // ── Section 10 – AI Tools (Clinical only) ─────────────────────────────
        body.Append(WordHelper.ELSection("10.", "Usage of AI Tools"));
        body.Append(WordHelper.WPara(
            "Our Firm may utilise artificial intelligence (AI) tools to assist in performing certain aspects of this engagement, including data analysis and document review. The use of such tools is subject to our Firm's quality management policies and procedures. All AI-assisted outputs are reviewed by appropriately qualified personnel before being relied upon. We will ensure that the use of AI tools complies with applicable ethical standards and does not compromise the confidentiality of information provided by ........ University.",
            afterPt: 6));

        // ── Section 11 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("11.", "Confidentiality"));
        body.Append(WordHelper.WPara(
            "We will keep confidential all information received in connection with this engagement and will not disclose it to any third party without the prior written consent of ........ University, except where disclosure is required by law, professional standards, or regulatory requirements. The obligation of confidentiality does not apply to information that is or becomes publicly available through no fault of ours.",
            afterPt: 6));

        // ── Section 12 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("12.", "Access to Records and Systems"));
        body.Append(WordHelper.WPara(
            "........ University will provide us with unrestricted access to all records, documentation, systems, and personnel that we may require to perform the agreed-upon procedures. Any restriction on access may affect the scope of our work and may require us to report accordingly.",
            afterPt: 6));

        // ── Section 13 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("13.", "Management Representation Letter"));
        body.Append(WordHelper.WPara(
            "Upon completion of the agreed-upon procedures, we will request a management representation letter from ........ University confirming the accuracy and completeness of information provided, the appropriateness of the agreed-upon procedures, and any other matters relevant to this engagement.",
            afterPt: 6));

        // ── Section 14 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("14.", "Reporting"));
        body.Append(WordHelper.WPara(
            "We will provide a report detailing the procedures performed and the factual findings resulting from those procedures. Our report will be addressed to ........ University and the DHET. We make no representation regarding the appropriateness of the agreed-upon procedures. Our report is not suitable for any other purpose and should not be used by, or distributed to, any party other than ........ University and the DHET.",
            afterPt: 6));

        // ── Section 15 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("15.", "Restriction on Use and Distribution"));
        body.Append(WordHelper.WPara(
            "Our report is solely for the purpose described in Section 2 and may not be suitable for any other purpose. It is intended solely for the use of ........ University and the DHET. ........ University agrees not to provide our report to any other party without our prior written consent.",
            afterPt: 6));

        // ── Section 16 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("16.", "Working Papers"));
        body.Append(WordHelper.WPara(
            "Our working papers are the property of our Firm and are prepared for the purpose of providing support for our report. They are subject to confidentiality provisions and will be retained in accordance with our Firm's policies and applicable professional standards.",
            afterPt: 6));

        // ── Section 17 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("17.", "Format of Report"));
        body.Append(WordHelper.WPara(
            "Our report will be issued in writing in the form of a formal Agreed-Upon Procedures report. The report will contain:\na) A description of the agreed-upon procedures performed;\nb) Factual findings resulting from each procedure;\nc) A statement that the engagement was conducted in accordance with ISRS 4400 (Revised);\nd) A statement that no opinion or assurance conclusion is expressed.",
            afterPt: 6));

        // ── Section 18 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("18.", "Change in Agreed Procedures"));
        body.Append(WordHelper.WPara(
            "Should either party wish to modify or change the agreed-upon procedures during the course of the engagement, such changes shall only be effective if agreed upon in writing by both parties. Additional procedures, if agreed, may result in additional fees.",
            afterPt: 6));

        // ── Section 19 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("19.", "Reliance on Information Provided"));
        body.Append(WordHelper.WPara(
            "We will rely on the information, records, and documentation provided to us by ........ University. We will not independently verify or audit such information beyond the scope of the agreed-upon procedures. Any inaccuracies or omissions in the information provided may affect our findings.",
            afterPt: 6));

        // ── Section 20 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("20.", "Our Personnel"));
        body.Append(WordHelper.WPara(
            "We will assign appropriately qualified and experienced personnel to perform this engagement. We reserve the right to reassign personnel as necessary, subject to maintaining the quality and continuity of the engagement.",
            afterPt: 6));

        // ── Section 21 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("21.", "Sub-Contracting"));
        body.Append(WordHelper.WPara(
            "We may, at our discretion, sub-contract certain aspects of this engagement to suitably qualified third parties. We will remain responsible for the quality of work performed by any sub-contractors and will ensure they are subject to the same confidentiality obligations as our Firm.",
            afterPt: 6));

        // ── Section 22 – Fees ─────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("22.", "Fees"));
        body.Append(WordHelper.WPara(
            "Our fees for this engagement are based on the time spent by our partners and professional staff. The billing schedule is as follows:",
            afterPt: 4));
        body.Append(WordHelper.SimpleTable(
            ["Engagement", "Billing Date", "Budget"],
            ["Clinical Training Enrolment Grant", "", ""]
        ));
        body.Append(WordHelper.Empty(4));
        body.Append(WordHelper.WPara(
            "The fees quoted are exclusive of Value Added Tax (VAT). VAT will be charged at the applicable rate.",
            afterPt: 6));

        // ── Section 23 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("23.", "Payment Terms"));
        body.Append(WordHelper.WPara(
            "Invoices are payable within 30 days of the invoice date. Should payment not be received within this period, we reserve the right to suspend our services until payment is received, without prejudice to our rights in respect of fees already incurred.",
            afterPt: 6));

        // ── Section 24 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("24.", "Intellectual Property"));
        body.Append(WordHelper.WPara(
            "All methodologies, tools, processes, and working papers developed or used in the course of this engagement remain the intellectual property of our Firm. ........ University is granted a non-exclusive licence to use our report solely for the purpose described in Section 2.",
            afterPt: 6));

        // ── Section 25 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("25.", "Limitation of Liability"));
        body.Append(WordHelper.WPara(
            "Our liability in connection with this engagement shall be limited to direct damages arising from our proven negligence or wilful misconduct. We shall not be liable for any indirect, consequential, or punitive damages. Our total aggregate liability shall not exceed the fees paid by ........ University for this engagement.",
            afterPt: 6));

        // ── Section 26 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("26.", "Professional Indemnity Insurance"));
        body.Append(WordHelper.WPara(
            "We maintain professional indemnity insurance as required by our professional regulatory body. Details of our insurance coverage are available upon request.",
            afterPt: 6));

        // ── Section 27 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("27.", "Data Privacy and Protection"));
        body.Append(WordHelper.WPara(
            "We will process any personal information received in connection with this engagement in accordance with the Protection of Personal Information Act, No. 4 of 2013 (POPIA) and applicable data protection legislation. We will implement appropriate technical and organisational measures to protect personal information against unauthorised access, disclosure, or loss.",
            afterPt: 6));

        // ── Section 28 ────────────────────────────────────────────────────────
        body.Append(WordHelper.ELSection("28.", "Dispute Resolution"));
        body.Append(WordHelper.WPara(
            "Any dispute arising out of or in connection with this engagement letter that cannot be resolved by negotiation between the parties shall be submitted to mediation. If mediation fails, the dispute shall be referred to arbitration in accordance with the Arbitration Foundation of South Africa (AFSA) rules. The arbitration shall be conducted in English and the seat of arbitration shall be Pretoria.",
            afterPt: 6));

        // ── Section 29 – Acceptance ───────────────────────────────────────────
        body.Append(WordHelper.ELSection("29.", "Acceptance"));
        body.Append(WordHelper.WPara(
            "Please confirm your acceptance of the terms and conditions of this engagement by signing and returning the attached copy of this letter. If the terms set out in this letter are not in accordance with your understanding of the engagement, please notify us immediately.",
            afterPt: 6));
        body.Append(WordHelper.WPara(
            "We look forward to working with ........ University on this engagement.",
            afterPt: 8));

        // ── Signature block ───────────────────────────────────────────────────
        body.Append(WordHelper.WPara("Yours sincerely,", afterPt: 6));
        body.Append(WordHelper.WPara("_______________________________________________", afterPt: 2));
        body.Append(WordHelper.WPara("Auditor Firm Name", bold: true, color: WordHelper.Purple, afterPt: 0));
        body.Append(WordHelper.WPara("Full Name CA(SA) RA", bold: true, color: WordHelper.Purple, afterPt: 2));
        body.Append(WordHelper.WPara("Division: Assurance", afterPt: 0));
        body.Append(WordHelper.WPara("Date: xx May xxxx", afterPt: 12));

        // ── Acceptance block ──────────────────────────────────────────────────
        body.Append(WordHelper.WPara("ACCEPTED AND AGREED on behalf of ........ University:", bold: true, afterPt: 6));
        body.Append(WordHelper.WPara("Signature: _______________________________________________", afterPt: 4));
        body.Append(WordHelper.WPara("Name: ___________________________________________________", afterPt: 4));
        body.Append(WordHelper.WPara("Designation: ____________________________________________", afterPt: 4));
        body.Append(WordHelper.WPara("Date: ___________________________________________________", afterPt: 4));

        body.Append(WordHelper.PageSetup());
        main.Document.Append(body);
        main.Document.Save();
    }
}
