using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HemisAudit.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers;

[Authorize]
public class HemisDraftReportController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Download()
    {
        var stream = new MemoryStream();
        BuildDocument(stream);
        stream.Position = 0;
        return File(stream,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "TUT_AUP_HEMIS_2025_Draft_Report.docx");
    }

    private static void BuildDocument(MemoryStream ms)
    {
        using var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();

        // ── Letterhead ──────────────────────────────────────────────────────────
        WordHelper.AddHeaderTable(body,
            ["The Council", "Tshwane University of Technology", "Private Bag X680", "Pretoria", "0001"],
            ["SNG Grant Thornton", "152, 14th Road", "Noordwyk", "Midrand, 1687", "T +27 (0) 86 117 6782"]);

        body.Append(WordHelper.Empty(8));

        // ── Title ───────────────────────────────────────────────────────────────
        body.Append(WordHelper.WPara(
            "Agreed-upon Procedures Report on the Higher Education Management Information System (HEMIS) for the period ending 31 December 2025.",
            bold: true, italic: true, color: WordHelper.Purple, sizePt: 10, afterPt: 8));

        // ── Disclaimer note ─────────────────────────────────────────────────────
        body.Append(WordHelper.WPara(
            "Please note that this Draft Report should be used as a guide only and may be amended at any time, as it was developed for guidance purposes.",
            italic: true, color: WordHelper.Purple, afterPt: 10));

        // ── Sections ─────────────────────────────────────────────────────────────
        WordHelper.AddSection(body, "Purpose of this Agreed-Upon Procedure Report and Restriction on Use and Distribution",
            "Our report is solely for the purpose of assisting the Tshwane University of Technology in determining whether the HEMIS data submitted to the Department of Higher Education and Training (DHET) are compliant with the requirements of the DHET Higher Education Management Information System (HEMIS) policy and may not be suitable for any other purpose. This report is intended solely for the use of the Tshwane University of Technology and the DHET, and should not be used by, or distributed to, any other parties.");

        WordHelper.AddSection(body, "Responsibility of the Engaging Party and the Responsible Party",
            "Tshwane University of Technology has acknowledged that the agreed-upon procedures are appropriate for the purpose of the engagement.\n\nThe Executive Director: Institutional Effectiveness & Technology, as identified by the Tshwane University of Technology, is responsible for the subject matter on which the agreed-upon procedures are performed.");

        WordHelper.AddSection(body, "Practitioner's responsibility",
            "We have conducted the agreed-upon procedures engagement in accordance with the International Standard on Related Services (ISRS) 4400 (Revised), Agreed-Upon Procedures Engagements. An Agreed-upon procedures engagement involves our performing the procedures that have been agreed with the Tshwane University of Technology, and reporting the findings, which are the factual results of the agreed upon procedures performed. We make no representation regarding the appropriateness of the agreed-upon procedures.\n\nThe agreed-upon procedures engagement is not an assurance engagement. Accordingly, we do not express an opinion or a conclusive assurance conclusion.\n\nHad we performed additional procedures, other matters might have come to our attention that would have been reported.");

        WordHelper.AddSection(body, "Professional Ethics and Quality Management",
            "We have complied with the ethical requirements in accordance with the International Ethics Standards Board for Accountants' International Code of Ethics for Professional Accountants (IESBA Code) and in accordance with other ethical requirements applicable to performing agreed-upon procedures engagements in South Africa.\n\nOur Firm applies International Standard on Quality Management 1 (ISQM1), Quality Management for firms that perform Audits or Reviews of Financial Statements, or Other Assurance or Related Services Engagements, which requires the firm to design, implement and operate a system of quality management including policies or procedures regarding compliance with ethical requirements, professional standards and applicable legal and regulatory requirements.");

        body.Append(WordHelper.WPara("Procedures and Findings", bold: true, afterPt: 4));
        body.Append(WordHelper.WPara(
            "We have performed the procedures described below, which were agreed upon with the Tshwane University of Technology management as delegated by The Council of the University in the engagement letter dated 14 May 2026.",
            afterPt: 8));

        // ── Procedures table ─────────────────────────────────────────────────────
        var tbl = WordHelper.CreateProcTable();

        // ── Section 3 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("3.  GENERAL PROCEDURES: SQLVALPAC FILES TO BE AUDITED"));

        tbl.Append(WordHelper.ProcDataRow("3.1",
            "Obtain the following from Statutory Reporting: Personnel in the Strategic Management Support Department:\n3.1.1 Qualification file\n3.1.2 Qualification CESM file\n3.1.3 Course file\n3.1.4 Credit value file\n3.1.5 Student file\n3.1.6 Course Registration file, and\n3.1.7 Staff Profile file",
            ("Obtained the following from Statutory Reporting: Personnel in the Strategic Management Support Department:\n3.1.1 Qualification file\n3.1.2 Qualification CESM file\n3.1.3 Course file\n3.1.4 Credit value file\n3.1.5 Student file\n3.1.6 Course Registration file, and\n3.1.7 Staff Profile file", false, (string?)null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("3.2",
            "3.2.1. Test the total population from the University's student database and agree the student number, student identification number, and qualification code to the VAPLAC database.",
            ("3.2.1. Tested the total population from the University's student production database and agreed the student number, student identification number, and qualification code to the VAPLAC database.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("3.2",
            "3.2.2. Test the whole population of staff that were on the university's staff database and agree the staff number, permanent \"P\" or temporary \"T\", gender, ethnic group, and birth date details to the VALPAC database.",
            ("3.2.2. Tested the whole population of staff that were on the University's staff production database and agreed the staff number, permanent \"P\" or temporary \"T\" status, gender, ethnic group, and birth date details to the VAPLAC database.", false, null),
            ("The following exception was noted where the staff date of birth in VALPAC did not agree with the staff date of birth in the University student database:", true, WordHelper.Purple),
            ("STAFF NO: 1013010  |  DATE OF BIRTH IN VALPAC: 1991028  |  DATE OF BIRTH IN PRODUCTION: 19991028", false, null),
            ("Management comment", true, null),
            ("Management acknowledges the finding relating to the incorrect birth date captured for the temporary employee appointed as a Student Mentor for a period of one month during 2025. During the appointment process, the date of birth was incorrectly captured as 01991028 instead of 19991028 on the ITS production system and therefore, the incorrect data was also extracted incorrectly to Valpac.\n\nThe data verification process for temporary staff was compromised following the 2024 cyberattack, where error reports on the HEDA Interrogator-Pro system were lost. While business continuity measures were implemented to ensure the continuation of critical operations, it has been realised that some verification controls have not yet been fully restored, so the data error was not identified during the 2025 HEMIS data clean-up process.\n\nThere is no validation report available on the ITS production system to detect such data-capture errors.\n\nThere is a different, more rigorous data verification process for permanent staff, whereby similar data-capture errors are identified timely, before the finalization of the HEMIS data.\n\nData verification and data quality measures will be enhanced with the redevelopment of specifically identified error reports that will be published on the HEDA Interrogator-Pro system and automatically distributed to the relevant staff members, daily for data correction. These measures will improve data integrity and reduce the risk of similar data errors occurring. Permanent staff data will be included in the error reports that will be redeveloped.\n\nThe data on Valpac and the ITS production system has been corrected, and the 2025 HEMIS staff database will be re-submitted to the DHET.\n\nThere are no HEMIS staff headcount implications for this data correction and no effect on permanent instruction and research staff data.", false, null)));

        // ── 3.3 ───────────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("3.3.  Deceased students"));

        tbl.Append(WordHelper.ProcDataRow("3.3.1",
            "Obtain a listing of deceased students from Statutory Reporting: Personnel in the Strategic Management Support Department:",
            ("Obtained a listing of deceased students from Statutory Reporting: Personnel in the Strategic Management Support Department:", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("3.3.2",
            "Verify that no students who are indicated as deceased before the academic year are included in the VALPAC database.",
            ("Verified that no students who are indicated as deceased before the academic year are included in the VALPAC database.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("3.3.3",
            "For the students identified in procedure 3.3.2. above, obtain death certificates from the Registrar's office to verify that the date of death is before or after the census date",
            ("For the students identified in procedure 3.3.2. above, obtained death certificates from the Registrar's office and verified that the date of death is before or after the census date.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        // ── Section 4 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("4.  INITIAL CHECKS ON QUALIFICATION, COURSE AND STUDENT FILE"));

        tbl.Append(WordHelper.ProcDataRow("4.1",
            "Test the total population from the Course file (CRSE) and perform the following:",
            ("Tested the total population from the Course file (CRSE) and performed the following:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("4.1.1",
            "For each course, search the Course file using the VAPLAC (Element 030) code for any duplicate course. If any duplicate course codes are identified this will indicate that there are courses that are not unique",
            ("For each course, searched the Course file using the VAPLAC (Element 030) code for any duplicate course codes.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.2",
            "Obtain a listing of census dates from the Registrar that includes the first teaching day to the last teaching day of the academic year, and perform the following procedures:",
            ("Obtained a listing of census dates from the Registrar that includes the first teaching day to the last teaching day of the academic year, and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("4.2.1",
            "Recalculate the census day as the midpoint of the first teaching day to the last teaching day of the academic period.",
            ("Recalculated the census day as the midpoint of the first teaching day to the last teaching day of the academic period.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.2.2",
            "Compare the recalculated census day to the census day as per the listing obtained in procedure 4.2 above and note any exceptions where the difference is more than 2 days.",
            ("Compared the recalculated census day to the census day as per the listing obtained in procedure 4.2 above and no exceptions where difference is more than 2 days were noted.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.3",
            "Select a sample of 40 students from the Course Registration file (CREG) and perform the following:",
            ("Selected a sample of 40 students from the Course Registration file (CREG) and performed the following:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("4.3.1",
            "Obtain from the Registrar and inspect the class list for continuous assessments, marks sheets for examination subjects, test and assignment marks, and the invigilator's mark sheet certificate setting out the students' name and student number and agree the student details such as student name and student number to indicate that the student was active.",
            ("Obtained from the Registrar and inspected the class list for continuous assessments, marks sheets for examination subjects, test and assignment marks, and the invigilator's mark sheet certificate setting out the students' name and student number and agreed the student details such as student name and student number to indicate that the student was active.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.3.2",
            "Inspect that the student has a final mark per the academic record.",
            ("Inspected that the student has a final mark per the academic record.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.4",
            "Obtain the following from Statutory Reporting: Student and Space in the Strategic Management Support Department:\n1. Correspondence with the Department of Higher Education and Training for approval of fatal errors.\n2. Latest run of the following VAPLAC summary validation reports:\na) STUD Validation Summary Report – Sub3\nb) QUAL Validation Summary Report – Sub3\nc) CRSE Validation Summary Report – Sub3\nd) CREG Validation Summary Report – Sub3\ne) CRED Validation Summary Report – Sub3\nf) CESM Validation Summary Report – Sub3\ng) PROF Validation Summary Report – Sub1",
            ("Obtained the following from Statutory Reporting Personnel in the Strategic Management Support Department:\n1. Correspondence with the Department of Higher Education and Training for approval of fatal errors.\n2. Latest run of the following VAPLAC summary validation reports:\na) STUD Validation Summary Report – Sub3\nb) QUAL Validation Summary Report – Sub3\nc) CRSE Validation Summary Report – Sub3\nd) CREG Validation Summary Report – Sub3\ne) CRED Validation Summary Report – Sub3\nf) CESM Validation Summary Report – Sub3\ng) PROF Validation Summary Report – Sub1", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.4.1",
            "Inspect if any fatal errors are listed in the latest run of the VAPLAC summary validation reports, except where they are approved by the DHET. Report on any fatal errors identified that were not approved by DHET via correspondence to the university.",
            ("Inspected whether any fatal errors were listed in the latest run of the VAPLAC summary validation reports, except where such errors were approved by the DHET. No fatal errors were identified in the latest VAPLAC summary validation reports, other than those explicitly approved by the DHET via correspondence to the university.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.4.2",
            "Select an exception sample of 25 students from the validation reports where the warning validation 00708 is identified and perform the following procedure:",
            ("Selected an exception sample of 25 students from the validation reports where the warning validation 00708 is identified and perform the following procedure:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("4.4.3",
            "Inspect, for each warning error 00708, that the qualification (element 001) in CREG agrees to the student's registration record and the Programme Qualification Mix (PQM).",
            ("Inspected, for each warning error 00708, that the qualification (element 001) in CREG agrees to the student's registration record and the Programme Qualification Mix (PQM).", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.5",
            "Obtain the following from Statutory Reporting: Student and Space in the Strategic Management Support Department:\n1. STUD ASCII file\n2. QUAL ASCII file\n3. CRSE ASCII file\n4. CRED ASCII file\n5. CREG ASCII file\n6. CESM ASCII file\n7. PROF ASCII file\n8. STUD Production file\n9. QUAL Production file\n10. CRSE Production file\n11. PROF Production file",
            ("Obtained the following from Statutory Reporting Personnel in the Strategic Management Support Department:\n1. STUD ASCII file\n2. QUAL ASCII file\n3. CRSE ASCII file\n4. CRED ASCII file\n5. CREG ASCII file\n6. CESM ASCII file\n7. PROF ASCII file\n8. STUD Production file\n9. QUAL Production file\n10. CRSE Production file\n11. PROF Production file", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.5.1",
            "Test the total population from the University's student database (production files related to STUD, QUAL, and CRSE files), STUD, QUAL, and CRSE ASCII files, and agree the student number, student identify number, and qualification code to the VAPLAC database.",
            ("Tested the total population from the University's student production database (production files related to STUD, QUAL and CRSE files), to STUD, QUAL and CRSE ASCII files, and agreed the student number, student identify number and qualification code to the VAPLAC database.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.5.2",
            "Test the whole population of staff in the PROF ASCII file, and agree with the staff number, permanent (P) or temporary (T), gender, ethnic group, and birth date details to the VALPAC database.",
            ("Tested the whole population of staff in the PROF ASCII file, and agreed with the staff number, permanent (P) or temporary (T) status, gender, ethnic group, and birth date details to the VAPLAC database.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("4.6",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and The Executive Director of Human Resources and document the responses obtained.",
            ("Obtained institutional responses for exceptions noted in the procedures above from The Registrar and The Executive Director of Human Resources and documented the responses obtained.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        // ── Section 5 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("5.  VAPLAC QUALIFICATION FILE AND QUALIFICATIONS CESM FILE"));

        tbl.Append(WordHelper.ProcDataRow("5.1",
            "Select a sample of 45 approved qualifications from the Qualification file (QUAL) and perform the following procedures:",
            ("Selected a sample of 45 approved qualifications from the Qualification file (QUAL) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("5.1.1",
            "Obtain from The Registrar the approval documents from the Minister of Higher Education, Science and Innovation for state funding and inspect (Element 004) Approval Status in the Qualification File reflects 'A'.",
            ("Obtained from The Registrar the approval documents from the Minister of Higher Education, Science and Innovation for state funding and inspected (Element 004) Approval Status in the Qualification File reflects 'A'.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.1.2",
            "Inspect that the correct qualification type code has been allocated to (Element 005) Qualification Type and agree that the university has allocated the correct qualification type according to the PQM.",
            ("Inspected that the correct qualification type code has been allocated to (Element 005) Qualification Type and agreed that the university has allocated the correct qualification type according to the PQM.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.1.3",
            "Inspect (Element 053) Minimum Time: Total and agree to the Total Subsidy Units: Total as per the PQM.",
            ("Inspected (Element 053) Minimum Time: Total and agree to the Total Subsidy Units: Total as per the PQM.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.1.4",
            "Inspect (Element 054) Minimum Time: Experiential and agree to the Total Subsidy Units: Work Integrated Learning (WIL/EL), in years as per the PQM.",
            ("Inspected (Element 054) Minimum Time: Experiential and agree to the Total Subsidy Units: Work Integrated Learning (WIL/EL), in years as per the PQM.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.1.5",
            "Obtain from The Registrar the policy under which the qualification has been approved and agree the coding as per (Element 084) Legacy Indicator/HEQF/HEQSF indicator to the policy.",
            ("Obtained from The Registrar the policy under which the qualification has been approved and agreed the coding as per (Element 084) Legacy/HEQF/HEQSF indicator to the policy.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.1.6",
            "Obtain the PQM document from The Registrar and agree (Element 090) Total Subsidy Units to the total subsidy units per the PQM document.",
            ("Obtained the PQM document from The Registrar and agreed (Element 090) Total Subsidy Units to the total subsidy units per the PQM document.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.2",
            "Select a sample of 45 qualifications from the Qualification CESM File (CESM) and perform the following procedures:",
            ("Selected a sample of 45 qualifications from the Qualification CESM File (CESM) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("5.2.1",
            "Obtain from The Registrar, the PQM which is the approval from the Minister of Higher Education, Science and Innovation for purposes of state funding. Agree the major field or fields of study noted in CESM to the approved PQM.",
            ("Obtained from The Registrar, the PQM which is the approval from the Minister of Higher Education, Science and Innovation for purposes of state funding. Agreed the major field or fields of study noted in CESM to the approved PQM.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.3",
            "Select a sample of 40 students by filtering on (Element 010) Entrance Category indicator for students on the student file (STUD) where the value is F (First time entering students) to obtain a list of qualifications for first-time entering students and perform the following procedures:",
            ("Selected a sample of 40 students by filtering on (Element 010) Entrance Category indicator for students on the student file (STUD) where the value is F (First time entering students) to obtain a list of qualifications for first-time entering students and perform the following procedures:", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.3.1",
            "Obtain from The Director: Quality Promotion Department the Category C\" non-aligned qualifications\" list; and",
            ("Obtained from The Director: Quality Promotion Department the Category C\" non-aligned qualifications\" list; and", false, null)));

        tbl.Append(WordHelper.ProcDataRow("5.3.2",
            "Compare the qualifications identified in procedure 5.3 above to the Category C\" non-aligned qualifications\" list and note if any of the qualifications identified in procedure 5.3.1 appeared in the Category C \"non-aligned qualifications\" list.",
            ("Compared the qualifications identified in procedure 5.3 above to the Category C\" non-aligned qualifications\" list and noted if any of the qualifications identified in procedure 5.3.1 appeared in the Category C \"non-aligned qualifications\" list.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("5.4",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 6 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("6.  VAPLAC COURSE FILE"));

        tbl.Append(WordHelper.ProcDataRow("6.1",
            "Select a sample of 55 courses from the Course File (CRSE) and perform the following procedures:",
            ("Selected a sample of 55 courses from the Course File (CRSE) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("6.1.1",
            "In respect of (Element 031) Course Approval Status, obtain from The Registrar the approved 2025 Prospectus. Inspect the approved 2025 Prospectus and note if the course appears in the curriculum of at least one qualification approved for state funding by the Minister of Higher Education, Science and Innovation.",
            ("In respect of (Element 031) Course Approval Status, obtained from The Registrar the approved 2025 Prospectus. Inspected the approved 2025 Prospectus and noted if the course appears in the curriculum of at least one qualification approved for state funding by the Minister of Higher Education, Science and Innovation.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6.1.2",
            "In respect of (Element 033) Course CESM confirm the course to the description of the 2nd order CESM code.",
            ("In respect of (Element 033) Course CESM confirmed the description of the course to the description of the 2nd order CESM code.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6.1.3",
            "In respect of (Element 034) Course Level Code agree the course level code to the course level category in the VALPAC help file.",
            ("In respect of (Element 034) Course Level Code agreed the course level code to the course level description in the VALPAC help file.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6.1.4",
            "In respect of (Element 062) Experiential Training Indicator inspect the approved 2025 Prospectus and note if the subject is approved for experiential training only and if qualification has been approved with the experiential training time in the PQM.",
            ("In respect of (Element 062) Experiential Training Indicator inspected the approved 2025 Prospectus and noted if the subject is approved for experiential training only and if qualification has been approved with the experiential training time in the PQM.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6.1.5",
            "In respect of (Element 091) Foundation Indicator obtain from the Deputy-Vice Chancellor: Teaching and Learning with Technology the policy document titled \"Foundation Provision in Ministerially approved programmes (15 May 2012)\". Inspect the policy document and note if the course is a foundation course and note if this is correctly reflected in element 091.",
            ("In respect of (Element 091) Foundation Indicator obtained from the Deputy-Vice Chancellor: Teaching and Learning with Technology the policy document titled \"Foundation Provision in Ministerially approved programmes (15 May 2012)\". Inspected the policy document and noted if the course is a foundation course and noted if this is correctly reflected in element 091.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("6.2",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 7 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("7.  SQLVAPLAC CREDIT VALUE FILE"));

        tbl.Append(WordHelper.ProcDataRow("7.1",
            "Select an exceptions sample of 40 courses from the Credit Value file (CRED) and perform the following procedures:",
            ("Selected an exceptions sample of 40 courses from the Credit Value file (CRED) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("7.1.1",
            "In respect of (Element 036) Course Credit Value recalculate the course credit values by dividing the NQF credits for the course by the total credits for the applicable year.",
            ("In respect of (Element 036) Course Credit Value recalculated the course credit values by dividing the NQF credits for the course by the total credits for the applicable year.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("7.1.2",
            "In respect of (Element 050) Completed Research Course Credit Value agree the research time for the relevant successfully completed research courses to the PQM and the 2025 approved Prospectus.",
            ("In respect of (Element 050) Completed Research Course Credit Value agreed the research time for the relevant successfully completed research courses to the PQM and the 2025 approved Prospectus.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("7.2",
            "Obtain from Statutory Reporting: Student and Space in the Strategic Management Support Department the results of the \"graduation test\" performed by the HEMIS extraction program on the ITS system which includes the FTE values before and after the adjustment and compare the total credit value for the selected qualification based on the factor list produced by the ITS HEMIS extraction program.",
            ("Obtained from Statutory Reporting: Student and Space in the Strategic Management Support Department the results of the graduation test performed by the HEMIS extraction program on the ITS production system which includes the credit values before and after the adjustment. Compared the total credit value for the selected qualification based on the factor list produced by the ITS HEMIS extraction program.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("7.3",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 8 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("8.  VAPLAC STUDENT FILE"));

        tbl.Append(WordHelper.ProcDataRow("8.1",
            "Select a sample of 60 students from the student file (STUD) and perform the following procedures:",
            ("Selected a sample of 60 students from the student file (STUD) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("8.1.1",
            "Obtain from The Registrar the student's signed registration form or audit trail of an online registration.",
            ("Obtained from The Registrar the student's signed registration form or audit trail of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.2",
            "In respect of (Element 001) Qualification code, agree the Qualification code per the student file (STUD) to the student's signed registration form or an audit trial of an online registration.",
            ("In respect of (Element 001) Qualification code, agreed the Qualification code per the student file (STUD) to the student's signed registration form or an audit trial of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.3",
            "In respect of (Element 013) Race agree the Race per the Student file (STUD) to the student's signed application and/or registration forms or proof of an online registration.",
            ("In respect of (Element 013) Race agreed the Race per the Student file (STUD) to the student's signed application and/or registration forms or proof of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.4",
            "In respect of (Element 014) Nationality, agree the student's nationality per the student file (STUD) to the student's signed application and/or registration form or proof of an online registration.",
            ("In respect of (Element 014) Nationality, agreed the student's nationality per the student file (STUD) to the student's signed application and/or registration form or proof of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.5",
            "In respect of (Element 010) Entrance Category, agree the student's entrance category per the student file (STUD) to the student's signed application and/or registration form or proof of an online registration.",
            ("In respect of (Element 010) Entrance Category, agreed the student's entrance category per the student file (STUD) to the student's signed application and/or registration form or proof of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.6",
            "In respect of (Element 022) Secondary Education obtain from The Registrar electronic data of matric results or any other documentary proof that the student has satisfied statutory entry requirements for admission. Inspect the documentary proof provided and agree that the student has satisfied statutory entry requirements for admission to the formal qualifications by inspecting the student's school leaver certificate for the tertiary education pass obtained.",
            ("In respect of (Element 022) Secondary Education obtained from The Registrar electronic data of matric results or any other documentary proof that the student has satisfied statutory entry requirements for admission. Inspected the documentary proof provided and agreed that the student has satisfied statutory entry requirements for admission to the formal qualifications by inspecting the student's school leaver certificate for the tertiary education pass obtained.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.1.7",
            "In respect of (Elements 026, 027, 028 and 029) Areas of Specialisation agree the student's area of specialisation per the student file to the student's signed application and/or registration form or proof of an online registration.",
            ("In respect of (Elements 026, 027, 028 and 029) Areas of Specialisation agreed the student's area of specialisation per the student file to the student's signed application and/or registration form or proof of an online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.2",
            "Select a sample of 40 graduates from the student file (STUD) where the value of element 025 is F and perform the following procedures:",
            ("Selected a sample of 40 graduates from the student file (STUD) where the value of element 025 is F and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("8.2.1",
            "In respect of (Element 025) Qualification fulfilled status obtain from the Registrar the student's academic record/ signed HOD letter and agree that the student completed the qualification by inspecting the student's academic record/ signed HOD letter.",
            ("In respect of (Element 025) Qualification fulfilled status obtain from the Registrar the student's academic record/ signed HOD letter and agree that the student completed the qualification by inspecting the student's academic record/ signed HOD letter.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.3",
            "Select a sample of 40 NSFAS students from the student file (STUD) where the value of element 019 is NS and perform the following procedures:",
            ("Selected a sample of 40 NSFAS students from the student file (STUD) where the value of element 019 is NS and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("8.3.1",
            "In respect of (Element 019) NSFAS Status, obtain from The Chief Financial Officer the funding list and remittance advice sent to the university by NSFAS Head Office. Inspect the funding list and remittance advice for the student's name and South African Identity Number to note if the student is funded by NSFAS.",
            ("In respect of (Element 019) NSFAS Status, we have obtained from The Chief Financial Officer the funding list and remittance advice sent to the university by NSFAS Head Office. Inspected the funding list and remittance advice for the student's name and South African Identity Number to note if the student is funded by NSFAS.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.4",
            "Select a sample of 10 Masters and Doctoral Students from the Student file (STUD) and perform the following procedure:",
            ("Selected a sample of 10 Masters and Doctoral Students from the Student file (STUD) and performed the following procedure:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("8.4.1",
            "In respect of (Element 073) Percentage Research Time for Masters and Doctoral Qualification, obtain from The Registrar the prospectus and agree the total research time.",
            ("In respect of (Element 073) Percentage Research Time for Masters and Doctoral Qualification, obtained from The Registrar the prospectus and agreed the total research time.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.5",
            "Select a sample of 10 \"Foundation student\" students and obtain from The Registrar the 2025 approved prospectus and agree the qualification per the student file to the 2025 approved prospectus.",
            ("In respect of (Element 106) Foundation Students, selected a sample of 10 Foundation Students and obtained from The Registrar the 2025 approved prospectus and agreed the qualification per the student file to the 2025 approved prospectus.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("8.6",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 9 ─────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("9.  VAPLAC COURSE REGISTRATION FILE"));

        tbl.Append(WordHelper.ProcDataRow("9.1",
            "Use the sample selected in procedure 4.3 above of 40 students from the Course Registration file (CREG) and perform the following procedures:",
            ("Used the sample selected in procedure 4.3 above of 40 students from the Course Registration file (CREG) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("9.1.1",
            "Obtain the following from The Registrar:\n-The student's signed application and/or registration form or proof of online registration\n-The student's examination scripts and student academic records; and\n-The electronic registration records",
            ("Obtained the following from The Registrar:\n-The student's signed application and/or registration form or proof of online registration\n-The student's examination scripts and student academic records; and\n-The electronic registration records", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.2",
            "In respect of (Element 064) Attendance mode for courses agree the attendance mode for the course per the Course Registration file (CREG) to the student's signed application and/or registration form or proof of online registration.",
            ("In respect of (Element 064) Attendance mode for courses agreed the attendance mode for the course per the Course Registration file (CREG) to the student's signed application and/or registration form or proof of online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.3",
            "In respect of (Element 018) Funding status obtain email correspondence from the Registrar stating that the student is not registered for the same course by another public institution as part of a collaboration agreement.",
            ("In respect of (Element 018) Funding status obtained email correspondence from the Registrar stating that the student is not registered for the same course by another public institution as part of a collaboration agreement.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.4",
            "In respect of (Element 030) Course Code agree the course code per the Course Registration file with the student's signed registration and/or change-of-course form or proof of online registration.",
            ("In respect of (Element 030) Course Code agreed the course code per the Course Registration file with the student's signed registration and/or change-of-course form or proof of online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.5",
            "In respect of (Element 032) Course completion status agree the course completion status per the Course Registration file to the status per the student academic records.",
            ("In respect of (Element 032) Course completion status agreed the course completion status per the Course Registration file to the status per the student academic records.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.6",
            "In respect of (Element 051) Examination – only indicator, agree the examination– only indicator per the Course Registration file to the registration records and student academic records.",
            ("In respect of (Element 051) Examination – only indicator, agreed the examination– only indicator per the Course Registration file to the registration records and student academic records.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.1.7",
            "In respect of (Element 001) Qualification code, agree the code to the student's signed application form or registration forms or proof of online registration.",
            ("In respect of (Element 001) Qualification code, agreed the code to the student's signed application form or registration forms or proof of online registration.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("9.2",
            "Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 11 ────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("11.  VAPLAC STAFF PROFILE"));

        tbl.Append(WordHelper.ProcDataRow("11.1",
            "Select a sample of 45 staff from Staff Profile file (PROF) and perform the following procedures:",
            ("Selected a sample of 45 staff from Staff Profile file (PROF) and performed the following procedures:", false, null)));

        tbl.Append(WordHelper.ProcDataRow("11.1.1",
            "Obtain from the Executive Director of Human Resources the following:\n- The employee's appointment letter\n- Personnel records that include information relating to the employee's gender, race, permanent/temporary status, staff qualifications and payroll code.",
            ("Obtained from the Executive Director of Human Resources the following:\n- The employee's appointment letter\n- Personnel records that include information relating to the employee's gender, race, permanent/temporary status, staff qualifications and payroll code.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("11.1.2",
            "In respect of (Element 039) Personnel Category agree the personnel category per the Staff Profile file (PROF) to the category per the employee's appointment letter.",
            ("In respect of (Element 039) Personnel Category agreed the personnel category per the Staff Profile file (PROF) to the category per the employee's appointment letter.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("11.1.3",
            "In respect of (Elements 012 Staff gender, 013 race, 041 permanent/temporary status, 046 staff qualification and 048 payroll code), agree the elements per the Staff Profile file (PROF) to the employee's personnel records.",
            ("In respect of (Elements 012 Staff gender, 013 race, 041 permanent/temporary status, 046 staff qualification and 048 payroll code), agreed the elements per the Staff Profile file (PROF) to the employee's personnel records.", false, null),
            ("", false, null),
            ("No Exceptions Noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("11.2",
            "Obtain institutional responses for exceptions noted in the procedures above from the Executive Director: Human Resources and document the responses obtained.",
            ("Not applicable as no exceptions were noted.", false, null)));

        // ── Section 12 ────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("12.  DATA CHANGE REQUESTS (To be performed if changes to the data are approved by the DHET)"));

        tbl.Append(WordHelper.ProcDataRow("12.1",
            "Obtain from Statutory Reporting: Personnel in the Strategic Management Support Department and inspect correspondence from the Department of Higher Education and Training (DHET) stating that the VAPLAC data should be corrected for the findings noted.",
            ("Obtained from Statutory Reporting: Personnel in the Strategic Management Support Department and inspected correspondence from the Department of Higher Education and Training (DHET) stating that the VAPLAC data should be corrected for the findings noted.", false, null),
            ("", false, null),
            ("No further differences were noted.", true, WordHelper.Purple)));

        tbl.Append(WordHelper.ProcDataRow("12.2",
            "Agree the column totals of the before and after VAPLAC Report Packs after the database updates and obtain institutional responses where there are differences.",
            ("Agreed the column totals of the before and after VAPLAC reports (Report pack) after the database updates and. No further differences were noted.", false, null)));

        // ── Section 13 ────────────────────────────────────────────────────────────
        tbl.Append(WordHelper.ProcSectionRow("13.  ATTACHMENTS TO AUDIT REPORT:"));

        tbl.Append(WordHelper.ProcDataRow("13.1",
            "Obtain from Statutory Reporting: Personnel in the Strategic Management Support Department the following VAPLAC reports (Report pack) from TUT after changes requested by DHET:\na) Funded credit report, contact-mode only, excluding experiential learning, including foundation.\nb) Funded credit report, contact-mode only, excluding experiential learning, including foundation (a) by Race, (b) by Nationality.\nc) Completed Funded credit report, contact-mode only, excluding experiential learning, including foundation.\nd) Funded credit report, other than contact-mode only, excluding experiential learning, including foundation.\ne) Completed funded credit report, other than contact-mode only, excluding experiential, including foundation.\nf) Funded credit report, other than contact-mode only, excluding experiential learning, foundation only.\ng) Funded credit report, contact-mode only, excluding experiential learning, foundation only.\nh) Unduplicated Headcount of enrolled students according to race, gender, home language and qualification type (Table 2.7) including the first time entering undergraduate enrolments.\ni) Fractional 1st order CESMs for all students Total (Table 2.12)\nj) Fractional 1st order CESMs for all students Contact only (Table 2.12)\nk) Fractional 1st order CESMs for all students Distance only (Table 2.12)\nl) Fractional 1st order CESMs for all students fulfilling requirements (Table 2.13)\nm) Headcount of permanent Staff by personnel category race and gender (Table 3.3)\nn) Headcount of instruction/research professionals with permanent appointments according to the highest most relevant qualification and rank (Table 3.4).\no) A table of Graduates for PGCE and the Bachelor of Education (only initial teacher education programmes).",
            ("Obtained from Statutory Reporting: Personnel in the Strategic Management Support Department the following VAPLAC reports (Report pack) from TUT after changes requested by DHET:\na) Funded credit report, contact-mode only, excluding experiential learning, including foundation.\nb) Funded credit report, contact-mode only, excluding experiential learning, including foundation (a) by Race, (b) by Nationality.\nc) Completed Funded credit report, contact-mode only, excluding experiential learning, including foundation.\nd) Funded credit report, other than contact-mode only, excluding experiential learning, including foundation.\ne) Completed funded credit report, other than contact-mode only, excluding experiential, including foundation.\nf) Funded credit report, other than contact-mode only, excluding experiential learning, foundation only.\ng) Funded credit report, contact-mode only, excluding experiential learning, foundation only.\nh) Unduplicated Headcount of enrolled students according to race, gender, home language and qualification type (Table 2.7) including the first time entering undergraduate enrolments.\ni) Fractional 1st order CESMs for all students Total (Table 2.12)\nj) Fractional 1st order CESMs for all students Contact only (Table 2.12)\nk) Fractional 1st order CESMs for all students Distance only (Table 2.12)\nl) Fractional 1st order CESMs for all students fulfilling requirements (Table 2.13)\nm) Headcount of permanent Staff by personnel category race and gender (Table 3.3)\nn) Headcount of instruction/research professionals with permanent appointments according to the highest most relevant qualification and rank (Table 3.4).\no) A table of Graduates for PGCE and the Bachelor of Education (only initial teacher education programmes).", false, null)));

        tbl.Append(WordHelper.ProcDataRow("13.2",
            "Stamp VAPLAC Report Pack.",
            ("Stamped the VAPLAC Report Pack.", false, null)));

        body.Append(tbl);
        body.Append(WordHelper.Empty(12));

        // ── Signature ──────────────────────────────────────────────────────────────
        body.Append(WordHelper.WPara("_______________________________________________", afterPt: 2));
        body.Append(WordHelper.WPara("SizweNtsalubaGobodo Grant Thornton Inc.", bold: true, color: WordHelper.Purple, afterPt: 0));
        body.Append(WordHelper.WPara("Nericha Moodley", bold: true, color: WordHelper.Purple, afterPt: 2));
        body.Append(WordHelper.WPara("Director", afterPt: 0));
        body.Append(WordHelper.WPara("Registered Auditor", afterPt: 6));
        body.Append(WordHelper.WPara("Date: xx July 2026", afterPt: 6));
        body.Append(WordHelper.WPara("152 14th Road Noordwyk", afterPt: 0));
        body.Append(WordHelper.WPara("Midrand, 1687", afterPt: 0));

        body.Append(WordHelper.PageSetup());
        main.Document.Append(body);
        main.Document.Save();
    }
}
