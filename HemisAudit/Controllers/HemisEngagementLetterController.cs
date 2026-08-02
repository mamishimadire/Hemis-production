using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HemisAudit.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HemisAudit.Controllers;

[Authorize]
public class HemisEngagementLetterController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Download()
    {
        var stream = new MemoryStream();
        BuildDocument(stream);
        stream.Position = 0;
        return File(stream,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "TUT_HEMIS_AUP_Engagement_Letter.docx");
    }

    private static void BuildDocument(MemoryStream ms)
    {
        using var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, true);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = new Body();

        // Letterhead
        WordHelper.AddHeaderTable(body,
            ["The Chief Financial Officer (CFO)", "Tshwane University of Technology", "Private Bag X680", "Pretoria", "0001"],
            ["SizweNtsalubaGobodo Grant Thornton", "152, 14th Road, Noordwyk", "Midrand, 1687", "T +27 (0) 12 443 6000", "sng-grantthornton.co.za"]);

        body.Append(WordHelper.Empty(8));
        body.Append(WordHelper.WPara(
            "Engagement letter: Higher Education Management Information System (HEMIS) – Agreed Upon Procedures for the period ending 31 December 2025.",
            bold: true, color: WordHelper.Purple, sizePt: 10, afterPt: 8));
        body.Append(WordHelper.WPara(
            "Please note that this Engagement letter should be used as a guide only and may be amended at any time, as it was developed for guidance purposes.",
            italic: true, color: WordHelper.Purple, afterPt: 10));

        // 1. Purpose
        body.Append(WordHelper.ELSection("1.", "Purpose"));
        body.Append(WordHelper.WPara(
            "This letter is to our understanding of the terms and objectives of our engagement and the nature and limitations of the services that we will provide. Our engagement will be conducted in accordance with the International Standard on Related Services (ISRS 4400 Revised) Engagements applicable to agreed-upon procedures engagements. The procedures we perform will not constitute an audit or a review made in accordance with International Standards on Review Engagements and consequently assurance will not be expressed.",
            afterPt: 6));

        // 2. Scope of the engagement
        body.Append(WordHelper.ELSection("2.", "Scope of the engagement"));
        body.Append(WordHelper.WPara("Approach:", bold: true, afterPt: 4));
        body.Append(WordHelper.WPara(
            "Use data analysis techniques to validate 100% of the population against the 2026 DHET-approved directives. Random sample testing on exceptions and directives requiring manual testing.",
            afterPt: 6));
        body.Append(WordHelper.WPara("You have requested that we perform the following procedures:", afterPt: 4));

        // Procedure group 3
        body.Append(WordHelper.WPara("3 General procedures : SQLVALPAC FILES TO BE AUDITED", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "3.1.  Obtain the following from Statutory Reporting: Personnel in the Strategic Management Support Department:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("3.1.1  Qualification and Qualification CESM files", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.1.2  Course file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.1.3  Credit value file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.1.4  Student file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.1.5  Course Registration file, and", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.1.6  Staff Profile file", leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("3.2.", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara(
            "3.2.1  Test the total population from the University's student database and agree with the student number, Student identification number, and qualification code to the VALPAC database.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "3.2.2  Test the whole population of staff that were on the university's staff database and agree the staff number, permanent “P” or temporary “T”, gender, ethnic group, and birth date details to the VALPAC database.",
            leftTwips: 540, afterPt: 4));

        // Procedure group 4
        body.Append(WordHelper.WPara("4 Initial checks on Qualification, Course and Student Files", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara("4.1  Coding of Courses", leftTwips: 360, bold: true, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "Test the total population from the Course file (CRSE) and perform the following;",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.1.1 ————— the idea is to check if all courses are unique",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "For each course, search the Course file using the VALPAC (Element 030) code for any duplicate course. If any duplicate course codes are identified this will indicate that there are courses that are not unique.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("4.2  Course census date", leftTwips: 360, bold: true, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "Obtain a listing of census dates from the Registrar that includes the first teaching day to the last teaching day of the academic year, and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.2.1  Recalculate the census day as the midpoint of the first teaching day to the last teaching day of the academic period.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.2.2  Compare the recalculated census day to the census day as per the listing obtained in procedure 4.2 above and note any exceptions where the difference is more than 2 days.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("4.3  Active Students—", leftTwips: 360, bold: true, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "Select a sample of 40 students from the Course Registration file (CREG) and perform the following:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.3.1  Obtain from the Registrar and inspect the class list for continuous assessments, marks sheets for examination subjects, test and assignment marks, and the invigilator’s mark sheet certificate setting out the students’ name and student number and agree the student details such as student name and student number to indicate that the student was active.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.3.2  Inspect that the student has a final mark per the academic record.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("4.4  SQLVALPAC error reports", leftTwips: 360, bold: true, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "Obtain the following from Statutory Reporting: Personnel in the Strategic Management Support Department:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("1.   Correspondence with the Department of Higher Education and Training for approval of fatal errors.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("2.   Latest run of the following VALPAC summary validation reports and perform the procedures below:", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.WPara("Validation Reports (VALPAC):", afterPt: 2));
        body.Append(WordHelper.IndentPara("a)   STUD Validation Summary Report – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("b)   QUAL Validation Summary Report – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("c)   CRSE Validation Summary Reports – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("d)   CREG Validation Summary Report – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("e)   CRED Validation Summary Report – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("f)    CESM Validation Summary Report – Sub3", leftTwips: 360, afterPt: 1));
        body.Append(WordHelper.IndentPara("g)   PROF Validation Summary Report – Sub1", leftTwips: 360, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "4.4.1  Inspect if any fatal errors are listed in the latest run of the VALPAC summary validation reports, except were approved by the DHET. Report on any fatal errors identified that were not approved by DHET via correspondence to the university.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.4.2  Select an exceptions sample of 15 students from the validation reports where the warning validation 00708 is identified and perform the following procedure:",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.4.3  Inspect, for each warning error 00708, that the qualification (element 001) in CREG agrees to the student’s registration record and the Programme Qualification Mix (PQM).",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("4.5  SQLVALPAC reports", leftTwips: 360, bold: true, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "Obtain the following from Statutory Reporting: Student and Space in the Strategic Management Support Department:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("1.    STUD ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("2.    QUAL ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("3.    CRSE ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("4.    CRED ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("5.    CREG ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("6.    CESM ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("7.    PROF ASCII file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("8.    STUD Production file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("9.    QUAL Production file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("10.  CRSE Production file", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("11.  PROF Production file", leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "4.5.1  Test the total population from the University’s student database (production files related to STUD, QUAL, and CRSE files), STUD, QUAL, and CRSE ASCII files, and agree the student number, student identify number, and qualification code to the VALPAC database.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "4.5.2  Test the whole population of staff in the PROF ASCII file, and agree with the staff number, permanent (P) or temporary (T), gender, ethnic group, and birth date details to the VALPAC database.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.WPara(
            "4.6   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and The Executive Director of Human Resources and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 5
        body.Append(WordHelper.WPara("5 VALPAC Qualification file and qualifications CESM File", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "5.1  Select a sample of 45 approved qualifications from the Qualification file (QUAL) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.1  Obtain from The Registrar the approval documents from the Minister of Higher Education, Science and Innovation for state funding and inspect (Element 004) Approval Status in the Qualification File reflects ‘A’.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.2  Inspect that the correct qualification type has been allocated to (Element 005) Qualification Type and agree that the university has allocated the correct qualification type according to the PQM.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.3  Inspect (Element 053) Minimum Time: Total and agree to the Total Subsidy Units: Total as per the PQM.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.4  Inspect (Element 054) Minimum Time: Experiential and agree to the Total Subsidy Units: Work Integrated Learning (WIL/EL), in years as per the PQM.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.5  Obtain from The Registrar the policy under which the qualification has been approved and agree the coding as per (Element 084) Legacy Indicator/HEQF/HEQSF indicator to the policy.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.1.6  Obtain the PQM document from The Registrar and agree (Element 090) Total subsidy units to the total subsidy units per the PQM document.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "5.2  Select a sample of 45 qualifications from the Qualification CESM File (CESM) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.2.1  Obtain from The Registrar, the PQM which is the approval from the Minister of Higher Education and Innovation for purposes of state funding. Agree the major field or fields of study noted in CESM to the approved PQM.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "5.3  Select a sample of 40 students by filtering on element 010 “First Time Entering” indicator for students on the student file (STUD) where the value is F (First time entering students) to obtain a list of qualifications for first-time entering students and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.3.1  Obtain from The Director: Quality Promotion Department the Category C “non-aligned qualifications” list; and",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "5.3.2  Compare the qualifications identified in procedure 5.3 above to the Category C “non-aligned qualifications” list and note if any of the qualifications identified in procedure 5.3.1 appeared in the Category C “non-aligned qualifications” list.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.WPara(
            "5.4   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 6
        body.Append(WordHelper.WPara("6 VALPAC COURSE FILE", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "6.1  Select a sample of 55 courses from the Course File (CRSE) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "6.1.1  In respect of (Element 031) Course Approval Status, obtain from The Registrar the approved 2025 Prospectus. Inspect the approved 2025 Prospectus and note if the course appears in the curriculum of at least one qualification approved for state funding by the Minister of Higher Education, Science and Innovation.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "6.1.2  In respect of (Element 033) Course CESM confirm the course to the description of the 2nd order CESM code.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "6.1.3  In respect of (Element 034) Course Level Code agree the course level code to the course level category in the VALPAC help file.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "6.1.4  In respect of (Element 062) Experiential Training Indicator inspect the approved 2025 Prospectus and note if the subject is approved for experiential training only and if qualification has been approved with the experiential training time in the PQM.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "6.1.5  In respect of (Element 091) Foundation Indicator obtain from the Deputy-Vice Chancellor: Teaching and Learning with Technology the policy document titled “Foundation provision in Ministerially approved programmes (15 May 2012)”. Inspect the policy document and note if the course is a foundation course and note if this is correctly reflected in element 091.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.WPara(
            "6.2   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 7
        body.Append(WordHelper.WPara("7 SQLVALPAC CREDIT VALUE FILE", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "7.1  Select an exceptions sample of 40 courses from the Credit value file (CRED) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "7.1.1  In respect of (Element 036) Course Credit Value recalculate the course credit values by dividing the NQF credits for the course by the total credits for the applicable year.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "7.1.2  In respect of (Element 050) Completed Research Course Credit Value agree the research time for the relevant successfully completed research courses to the PQM and the 2025 approved prospectus.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "7.2  Obtain from Statutory Reporting: Student and Space in the Strategic Management Support Department the results of the “graduation test performed by the HEMIS extraction program on the ITS system which includes the FTE values before and after the adjustment and compare the total credit value for the selected qualification based on the factor list produced by the ITS HEMIS extraction program.",
            leftTwips: 360, afterPt: 3));
        body.Append(WordHelper.WPara(
            "7.3   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 8
        body.Append(WordHelper.WPara("8 VALPAC STUDENT FILE", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "8.1  Select a sample of 60 students from the student file (STUD) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.1  Obtain from The Registrar the student’s signed registration form or audit trail of an online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.2  In respect of (Element 001) Qualification code, agree the Qualification code per the Student file (STUD) to the student’s signed registration form or an audit trial of an online registration",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.3  In respect of (Element 013) Race agree the Race per the Student file (STUD) to the student’s signed application and/or registration forms or proof of an online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.4  In respect of (Element 014) Nationality, agree the student’s nationality per the student file (STUD) to the student’s signed application and/or registration form or proof of an online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.5  In respect of (Element 010) Entrance Category, agree the student’s entrance category per the student file (STUD) to the student’s signed application and/or registration form or proof of online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.6  In respect of (Element 022) Secondary Education obtain from The Registrar electronic data of matric results or any other documentary proof that the student has satisfied statutory entry requirements for admission. Inspect the documentary proof provided and agree that the student has satisfied statutory entry requirements for admission to the formal qualifications by inspecting the student’s school leaver certificate for the tertiary education pass obtained.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.1.7  In respect of (Elements 026, 027, 028 and 029) Areas of Specialisation agree the student’s area of specialisation per the student file to the student’s signed application and/or registration form or proof of an online registration.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "8.2  Select a sample of 40 graduates from the student file (STUD) where the value of element 025 is F and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.2.1  In respect of (Element 025) Qualification fulfilled status obtain from The Registrar the student’s academic record/ signed HOD letter and agree that the student completed the qualification by inspecting the student’s academic record/ signed HOD letter.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "8.3  Select a sample of 40 NSFAS students from the student file (STUD) where the value of element 019 is NS and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.3.1  In respect of (Element 019) NSFAS Status, obtain from The Chief Financial Officer the funding list and remittance advice sent to the university by NSFAS Head Office. Inspect the funding list and remittance advice for the student’s name and South African Identity Number to note if the student is funded by NSFAS.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "8.4  Select a sample of 10 Masters and Doctoral Students from the Student file (STUD) and perform the following procedure:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "8.4.1  In respect of (Element 073) percentage research time for Masters and Doctoral Qualification, obtain from The Registrar the prospectus and agree the total research time.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "8.5  Select a sample of 10 “Foundation student” students and obtain from The Registrar the 2025 approved prospectus and agree the qualification per the student file to the 2025 approved prospectus.",
            leftTwips: 360, afterPt: 3));
        body.Append(WordHelper.WPara(
            "8.6   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 9
        body.Append(WordHelper.WPara("9 VALPAC COURSE REGISTRATION FILE", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "9.1  Use the sample selected in procedure 4.3 above of 40 students from the Course Registration file (CREG) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("9.1.1  Obtain from The Registrar:", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("-    The student’s signed application and/or registration form or proof of online registration", leftTwips: 720, afterPt: 1));
        body.Append(WordHelper.IndentPara("-    The student’s examination scripts and student academic records; and", leftTwips: 720, afterPt: 1));
        body.Append(WordHelper.IndentPara("-    The electronic registration records.", leftTwips: 720, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.2  In respect of (Element 064) Attendance mode for courses agree the attendance mode for the course per the Course Registration file (CREG) to the student’s signed application and/or registration form or proof of online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.3  In respect of (Element 018) Funding status obtain email correspondence from the Registrar stating that the student is not registered for the same course by another public institution as part of a collaboration agreement.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.4  In respect of (Element 030) Course Code agree the course code per the Course Registration file with the student’s signed registration and/or change–of–course form or proof of online registration.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.5  In respect of (Element 032) Course completion status agree the course completion status per the Course Registration file to the status per the student academic records.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.6  In respect of (Element 051) Examination – only indicator, agree the examination– only indicator per the Course Registration file to the registration records and student academic records.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "9.1.7  In respect of (Element 001) Qualification code, agree the code to the student’s signed application form or registration forms or proof of online registration.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.WPara(
            "9.2   Obtain institutional responses for exceptions noted in the procedures above from The Registrar and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 11
        body.Append(WordHelper.WPara("11 VALPAC STAFF PROFILE", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "11.1  Select a sample of 45 staff from the Staff profile file (PROF) and perform the following procedures:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("11.1.1  Obtain from the Executive Director of Human Resources the following:", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("-    The employee’s appointment letter", leftTwips: 720, afterPt: 1));
        body.Append(WordHelper.IndentPara("-    Personnel records that include information relating to the employee’s gender, race, permanent/temporary status, staff qualifications and payroll code.", leftTwips: 720, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "11.1.2  In respect of (Element 039) Personnel category agree the personnel category per the Staff profile file (PROF) to the category per the employee’s appointment letter.",
            leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "11.1.3  In respect of (Elements 012 Staff gender, 013 race, 041 permanent/temporary status, 046 staff qualification and 048 payroll code), agree the elements per the Staff profile file (PROF) to the employee’s personnel records.",
            leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.WPara(
            "11.2  Obtain institutional responses for exceptions noted in the procedures above from The Executive Director: Human Resources and document the responses obtained.",
            bold: true, color: "2E74B5", afterPt: 5));

        // Procedure group 12
        body.Append(WordHelper.WPara("12. DATA CHANGE REQUESTS (To be performed if changes to the data are approved by the DHET)", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.IndentPara(
            "12.1  Obtain from Statutory Reporting: Personnel in the Strategic Management Support Department and inspect correspondence from the Department of Higher Education and Training (DHET) stating that the VALPAC data should be corrected for the findings noted.",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "12.2  Agree the column totals of the before and after VALPAC Report Packs after the database updates and obtain institutional responses where there are differences.",
            leftTwips: 360, afterPt: 4));

        // Procedure group 13
        body.Append(WordHelper.WPara("13 ATTACHMENTS TO AUDIT REPORT", bold: true, color: WordHelper.Purple, afterPt: 3));
        body.Append(WordHelper.WPara("Perform the following procedures:", afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "13.1  Obtain from Statutory Reporting: Personnel in the Strategic Management Support Department the following VALPAC reports (Report pack) from TUT after changes requested by DHET:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("a)   Funded credit report, contact-mode only, excluding experiential learning, including foundation.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("b)   Funded credit report, other than contact-mode only, excluding experiential learning, including foundation (a) by Race, (b) by Nationality.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("c)   Completed Funded credit report, contact-mode only, excluding experiential learning, including foundation.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("d)   Funded credit report, other than contact-mode only, excluding experiential learning, including foundation.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("e)   Completed funded credit report, other than contact-mode only, excluding experiential learning, including foundation.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("f)    Funded credit report, contact-mode only, excluding experiential learning, foundation only.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("g)   Funded credit report, other than contact-mode only, excluding experiential learning, foundation only.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("h)   Unduplicated Headcount of enrolled students according to race, gender, home language and qualification type (Table 2.7) including the first time entering undergraduate enrolments.", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("i)    Fractional 1st order CESMs for all students Total (Table 2.12)", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("j)    Fractional 1st order CESMs for all students Contact only (Table 2.12)", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("k)   Fractional 1st order CESMs for all students Distance only (Table 2.12)", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("l)    Fractional 1st order CESMs for all students fulfilling requirements (Table 2.12)", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("m)  Headcount of permanent Staff by personnel category race and gender (Table 3.3)", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("n)   Headcount of instruction/research professionals with permanent appointments according to the highest most relevant qualification and rank (Table 3.4).", leftTwips: 540, afterPt: 1));
        body.Append(WordHelper.IndentPara("o)   A table of Graduates for PGCE and the Bachelor of Education (only initial teacher education programmes).", leftTwips: 540, afterPt: 3));
        body.Append(WordHelper.IndentPara("13.2  Stamp VALPAC Report Pack.", leftTwips: 360, afterPt: 6));

        // 3. Responsibilities of the University
        body.Append(WordHelper.ELSection("3.", "Responsibilities of the University"));
        body.Append(WordHelper.WPara(
            "Tshwane University of Technology acknowledges that the procedures are appropriate for the purpose of the engagement. This agreed-upon procedures engagement will be conducted on the basis that the University is responsible for the subject matter on which the agreed-upon procedures are performed. The responsibility of determining the adequacy or otherwise of the procedures agreed to be performed by us is that of Management.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "We will make no representation regarding the sufficiency of the procedures either for the purpose for which our report is being prepared or for any other purpose. In addition, we have no obligation to perform any procedures beyond those referred above. We have no responsibility to update our report for any events or circumstances occurring after the date of such reports.",
            afterPt: 6));

        // 4. Non-compliance
        body.Append(WordHelper.ELSection("4.", "Non – compliance with laws and regulations"));
        body.Append(WordHelper.WPara(
            "It is the responsibility of management, with the oversight of those charged with governance, to ensure that the entity’s operations are conducted in accordance with the provisions of laws and regulations, including compliance with the provisions of laws and regulations that are applicable to statutory reporting.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Management is responsible for establishing and maintaining internal control to provide reasonable assurance with regard to the reliability of statutory reporting, effectiveness and efficiency of operation.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our responsibility is not to prevent noncompliance with laws and regulations, and we cannot be expected to detect noncompliance with all laws and regulations.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our Code of professional conduct further requires us, once aware of non-compliance or suspected non-compliance with laws and regulations, to discuss the matter with the appropriate level of management and, where appropriate, those charged with governance, advise them to take appropriate and timely actions, assess the appropriateness of the response and determine if further action is needed in the public interest.",
            afterPt: 4));
        body.Append(WordHelper.WPara("We may also discuss the matter with internal auditors, where applicable.", afterPt: 6));

        // 5. POPI Act
        body.Append(WordHelper.ELSection("5.", "Protection of Personal Information (POPI) Act"));
        body.Append(WordHelper.WPara(
            "The client acknowledges that the practitioner cannot perform its obligations under this Agreement without Processing certain Personal Information¹, including the personal provided by the Client to the Practitioner (the “Client Data”).",
            afterPt: 4));
        body.Append(WordHelper.WPara("The Client acknowledges that:", afterPt: 4));
        body.Append(WordHelper.IndentPara(
            "•   the Processing of the Client Data is necessary and requisite as a legal and regulatory requirement in the conduct of an audit by the Practitioner.",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "•   it has been advised of the purpose and reason for the collection and processing of the Client Data; and",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "•   the audit tools, software and methodology of Grant Thornton International Limited is used in the conduct of the audit by the Practitioner and, as a result, the Practitioner will transfer and subsequently Process the Client Data at a Microsoft Azure data centres located in Europe.",
            leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "¹ “Personal Information” has the meaning given to it POPIA.",
            italic: true, sizePt: 8, afterPt: 4));
        body.Append(WordHelper.WPara(
            "The Parties record that the Practitioner will Process² the Client Data in accordance with the provisions of this Agreement. When Processing the Client Data, the Practitioner will take all reasonable and appropriate technical and organisational precautions and measures necessary to prevent any (i) loss of, damage to, or unauthorised destruction of the Client Data; or (ii) unauthorised or unlawful access to or Processing of the Client Data. For this purpose, the Practitioner will:",
            afterPt: 4));
        body.Append(WordHelper.IndentPara("•   identify all reasonably foreseeable internal and external risks to Customer Data in its possession or under its control.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   establish and maintain appropriate safeguards against the risks identified.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   regularly verify that the safeguards are effectively implemented; and", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   ensure that the safeguards are continually updated in response to new risks or deficiencies in previously implemented safeguards.", leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "The Client hereby warrants, represents and undertakes that in respect of all Client Data, all the consents necessary to ensure compliance by the Client and the Practitioner with applicable laws, including Data Protection Legislation³ have been obtained from the person or entity to whom such Personal Information relates, as well as any regulators or other third parties, in relation to:",
            afterPt: 4));
        body.Append(WordHelper.IndentPara("•   the transmission by the Client to the Practitioner in accordance with this Agreement or otherwise permitted by law;", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   the transmission by the Client or the Practitioner of the Client Data to third parties in accordance with this Agreement or otherwise permitted by law; and", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   the Processing by the Practitioner of any Client Data received by the Practitioner from the Client in any country in which the Client Data is held by the Practitioner", leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "The Client hereby indemnifies and holds the Practitioner harmless from and against all losses, damages, costs, expenses, penalties and fines that the Practitioner may sustain or incur arising from a breach by the Client of this clause or any other claim that may arise in respect of the Client Data (save to the extent that such a claim arises from a breach by the Practitioner of the provisions of this clause).",
            afterPt: 4));
        body.Append(WordHelper.WPara("Both Parties will ensure that personal information shared amongst them will be Processed in accordance with the provisions of POPIA⁴.", afterPt: 4));
        body.Append(WordHelper.WPara(
            "² “Process” means collect, receive, record, organise, collate, store, develop, update, modify, use, disseminate or perform any other act or action, including any other act or action which may be treated or defined as Processing in terms of POPIA, and the word “Processed” shall have a corresponding meaning.",
            italic: true, sizePt: 8, afterPt: 2));
        body.Append(WordHelper.WPara(
            "³ “Data Protection Legislation” means any and all laws, including, without limitation, regulations, directives, professional rules or any other requirements of government or any government agency, body or authority, or any regulatory or course, pertaining or relating to the protection or confidentiality of data or of Personal Information, including POPIA.",
            italic: true, sizePt: 8, afterPt: 2));
        body.Append(WordHelper.WPara(
            "⁴ “POPIA” means the Protection of Personal Information Act, 2013",
            italic: true, sizePt: 8, afterPt: 6));

        // 6. FICA
        body.Append(WordHelper.ELSection("6.", "Financial Intelligence Centre Act (FICA)"));
        body.Append(WordHelper.WPara(
            "In terms of Section 29 of the Financial Intelligence Centre Act, 38 of 2001, as amended (“FICA”) we are required by law to report to the Financial Intelligence Centre certain suspicious or unusual transactions, such as those which may involve money laundering, which have no apparent business or lawful purpose, or which may be relevant to an investigation of evasion or attempted evasion of tax. This statutory requirement which applies to both prospective and existing clients, overrides the professional ethics rules of confidentiality, which we observe.",
            afterPt: 6));

        // 7. Staff
        body.Append(WordHelper.ELSection("7.", "Staff"));
        body.Append(WordHelper.WPara(
            "Our staff members undergo periodic training and this, together with the taking of annual leave, may lead to staff turnover and lack of continuity. We will use our best endeavors to avoid any disruption to an engagement’s progress. Save as envisaged below, you agree not to make any offer of employment or to otherwise interfere with or entice away from the employment of any persons employed by SizweNtsalubaGobodo Grant Thornton. You further agree not to use such person’s services as an independent consultant or via a third party for a period of 12 months following the end of such person’s involvement, without the prior written consent of SizweNtsalubaGobodo Grant Thornton. This consent may not be unreasonably withheld.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Should you make any offer of employment to any person currently employed by SizweNtsalubaGobodo Grant Thornton or who was employed by SizweNtsalubaGobodo Grant Thornton for the immediately preceding 12 month period from the date of such offer of employment, you will be liable for and will pay to SizweNtsalubaGobodo Grant Thornton a placement fee equal to 25% of such employee’s total annual cost to entity, excluding Value Added Tax (“VAT”).",
            afterPt: 6));

        // 8. Communication with management
        body.Append(WordHelper.ELSection("8.", "Communication with management"));
        body.Append(WordHelper.WPara(
            "We will communicate only those matters of governance interest that comes to our attention as a result of the performance of the agreed – upon procedures. We are not required to design procedures for the specific purpose of identifying matters of governance interest.",
            afterPt: 6));

        // 9. Information
        body.Append(WordHelper.ELSection("9.", "Information"));
        body.Append(WordHelper.WPara(
            "To enable us to perform the services, you will use your best endeavours to procure and to supply promptly all information and assistance, and all access to documentation in your possession, custody, or under your control, and to personnel under your control, where required by us. Where such information and/or documentation is not in your possession or custody, or under your control, you will use your best endeavours to procure the supply of the information, assistance and/or access to all the documentation. We may rely on any instructions or requests made or notices given or information supplied, whether orally or in writing, by any person whom we know to be or reasonably believe to be authorised by you to communicate with us for such purposes (“an authorised person”). We may receive information from you or from other sources in the course of delivering the services and:",
            afterPt: 4));
        body.Append(WordHelper.IndentPara("a.   we will consider the consistency and quality of information received by us;", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("b.   not seek to establish the reliability of information received from you or any other information source. Accordingly, we assume no responsibility and make no representations with respect to the accuracy, reliability or completeness of any information provided to us; and", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("c.   we will not be liable for any loss or damage suffered by you arising from fraud, misrepresentation, withholding of information material to the services, or other default relating to such material information, whether on your part or that of the other information sources.", leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "You undertake to supply information in response to our enquiries to enable us to comply with our statutory obligations relating to the FICA and the Prevention of Organised Crime Act, (POCA).121 of 1998, as amended.",
            afterPt: 6));

        // 10. Meetings
        body.Append(WordHelper.ELSection("10.", "Meetings"));
        body.Append(WordHelper.WPara(
            "To provide an opportunity for you to discuss the matters raised in our various reports, we expect (where possible) to attend the university audit committees prior to the commencement of our engagement.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "You may also schedule meeting with us to discuss any matters that are pertinent to the engagement. Regular progress meetings will be held in addition to the AUP planning and draft audit report review meetings.",
            afterPt: 6));

        // 11. Third party rights
        body.Append(WordHelper.ELSection("11.", "Third party rights"));
        body.Append(WordHelper.WPara("The service contract will not create or give rise to, nor will it be intended to create or give rise to, any third-party rights.", afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our report is intended for the benefit of those to whom it is addressed. The engagement will not be planned or conducted in contemplation of reliance by any third party or with respect to any specific transaction. Therefore, items of possible interest to a third party will not be specifically addressed and matters may exist that would be assessed differently by a third party, possibly in connection with a specific transaction.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Any contractual arrangements between you and a third party which seek to impose such requirements upon us will not, as a matter of law, be binding on us. The Entity agrees that it will not seek us to commit to providing reports to third parties unless we have consented to do so in advance. We may decline to provide reports to third parties, save for those reports required by law or regulations. We will stipulate the terms upon which those reports will be provided should we agree to provide such reports. The Entity will assist us in agreeing the terms upon which we will report to third parties. Any such possible requirements must be discussed with us at the earliest opportunity.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Where we agree to provide reports to third parties, it remains the Entity’s responsibility to provide us with copies of the relevant contract documents and with any further information or explanations we may require, enabling us to prepare our report.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "We will not accept or assume responsibility (legal or otherwise) or accept liability for or in connection with any other purpose for which our report may be used, or to any other person to whom our report is shown or into whose hands it may come, and no other persons shall be entitled to rely on our report save where they have obtained our prior written consent that they may do so. If we have to accept responsibility to the third party, we will require their acceptance of limitation of liability as a condition of providing a report to them and reserve the right to charge additional fees.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "You will indemnify SizweNtsalubaGobodo Grant Thornton contracting party and any SizweNtsalubaGobodo Grant Thornton persons and hold them harmless against any loss, damage, expense or liability incurred by the parties and/or persons as a result of, arising from, or in connection with a combination of the following two circumstances:",
            afterPt: 2));
        body.Append(WordHelper.IndentPara("a.   Any breach by you of your obligations under the service contract; and", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("b.   Any claim made by a third party or any other beneficiaries which results from or arises from or is connected with any such breach.", leftTwips: 360, afterPt: 6));

        // 12. Electronic communications
        body.Append(WordHelper.ELSection("12.", "Electronic communications"));
        body.Append(WordHelper.WPara(
            "We may choose to communicate with you by electronic mail or internet where an authorised person wishes us to do so, on the basis that in consenting to this method of communication, you accept the inherent risks of such communications (including the security risks of interception of or unauthorised access to such communications, the risks of corruption of such communications, the risk of errors or loss of information and the risks of viruses or other harmful devices) and that you will perform virus checks. We will use commercially reasonable procedures to check for the most commonly known viruses before sending information electronically.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "We recognise that systems and procedures cannot be a guarantee that transmissions will be unaffected by such hazards. We confirm that we each accept the risks of and authorise electronic communications between us. We each agree to use commercially reasonable procedures to check for the then most commonly known viruses before sending information electronically and to safeguard the security and confidentiality of the information transmitted, but we cannot guarantee that the transmission will be free of infection nor its security and confidentiality.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "We shall each be responsible for protecting our own systems and interests in relation to electronic communications and the Entity and SizweNtsalubaGobodo Grant Thornton (in each case including our respective partners/directors, employees or agents) shall have no liability to each other on any basis, whether in contract, delict (including negligence) or otherwise, in respect of any error, damage, loss or omission arising from or in connection with the electronic communication of information between us and our reliance on such information.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "The exclusion of liability in the previous clause shall not apply to the extent that any liability arises out of acts, omissions or misrepresentations which are in any case criminal, dishonest or fraudulent on the part of our respective partners/directors, employees, or agents. If our communication relates to a matter of significance on which you wish to rely and you are concerned about the possible effects of electronic transmission, you should request a hard copy of such transmission from us. If you wish to password protect all or certain documents transmitted, you may request us to do so.",
            afterPt: 6));

        // 13. Reporting
        body.Append(WordHelper.ELSection("13.", "Reporting"));
        body.Append(WordHelper.WPara(
            "At the conclusion of our engagement, we will prepare a report based on the findings of the agreed upon procedures. The form and content of our report may need to be amended in the light of our engagement findings.",
            afterPt: 6));

        // 14. Ownership and access to files
        body.Append(WordHelper.ELSection("14.", "Ownership and access to files"));
        body.Append(WordHelper.WPara(
            "The working papers and files for this engagement created by us during the course of the engagement, including electronic documents and files, are the sole property of SizweNtsalubaGobodo Grant Thornton and you have no right to access them. We may decide in our own sole discretion to grant access to you to our working papers, should you wish to. We have set quality control policies for the retention of documentation, after which time we will commence the process of destroying the contents of our engagement files. To the extent we accumulate any of your original records during the engagement; those documents will be returned to you promptly upon completion of the engagement.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "We will retain ownership of the copyright and all other intellectual property rights in the product of the services, whether oral or tangible, including written advice, methodologies, software, systems know how and working papers. For the purposes of delivering services to you or other clients, we will be entitled to use or develop knowledge, experience and skills of general application gained through performing the services. You agree to keep confidential any methodologies and technology used by us to carry out our services. If you wish to distribute copies of any of these materials, this will require our prior written permission. We have the right to use your name as a reference in proposals or other similar submissions to other prospective clients, unless you specifically withhold permission for such disclosure. If we wish to use details of the work done for you for reference purposes, we will obtain your permission in advance.",
            afterPt: 6));

        // 15. Limitation of liability
        body.Append(WordHelper.ELSection("15.", "Limitation of liability"));
        body.Append(WordHelper.WPara(
            "The maximum liability of SizweNtsalubaGobodo Grant Thornton their partners, directors, employees and agents for all claims arising out of services provided in connection with this engagement shall be limited to the total fees charged for all services provided in connection with this engagement. This maximum liability shall be an aggregate liability for all claims from whatsoever source and howsoever arising, whether in contract, delict or otherwise.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "The Firm will not be liable to (Insert Client Name) any cessionary or third party claiming through or on behalf of (Insert Client Name) or any punitive damages whatsoever or for any consequential or other loss or damages beyond the maximum liability specified. This engagement is governed by South African law and any claims will be subject to the exclusive jurisdiction of the Courts of South Africa.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Any claims, however arising, must be commenced formally by service of summons or appropriate process by following necessary steps to initiate arbitration proceedings within three years after the party bringing the claim becomes aware (or ought reasonably to have become aware) of the facts which give rise to the claim and, in any event regardless of the knowledge of the Claimant, by no later than three years after the date of any alleged breach of contract, delictual act or other act or omission giving rise to a cause of action. This expressly overrides any statutory provision that would otherwise apply.",
            afterPt: 6));

        // 16. Timetable
        body.Append(WordHelper.ELSection("16.", "Timetable"));
        body.Append(WordHelper.WPara(
            "We will agree on a timetable to enable you to meet your statutory obligations to issue annual financial statements and to meet any other deadlines you have brought to our attention. However, any such timetable will be based on the assumption that we will receive the appropriate cooperation and assistance to perform an effective and efficient engagement.",
            afterPt: 6));

        // 17. Fees
        body.Append(WordHelper.ELSection("17.", "Fees"));
        body.Append(WordHelper.WPara(
            "We will render invoices in respect of the services comprising fees, disbursements and VAT thereon (where appropriate), together with any other foreign taxes (if applicable) (‘fees’) that might be payable thereon.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our fees are based on the time spent on your affairs by our partners/directors and staff, and on the levels of skill and responsibility involved, the nature and complexity of the services and the resources required to complete the engagement. These fees may differ from estimates that may have been supplied, of which estimates will only be provisional. Stringent reporting requirements or deadlines imposed by you might require work to be carried out at a higher level than usual or outside normal working hours, which may result in increased costs. Additional fees may also result from material changes in the services or from difficulties in obtaining information, which could not reasonably have been foreseen.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our invoices will be rendered at appropriate intervals during the course of the engagement or relevant assignment and is due on presentation of our invoices. We may recover any costs we incur in recovering from you any fees and/or disbursements as aforesaid.",
            afterPt: 4));
        body.Append(WordHelper.WPara("Fees are calculated either:", afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "a.   on an hourly basis at charge out rates applicable to the person undertaking the work. Stringent reporting requirements or deadlines imposed by you might require work to be carried out at a higher level than usual or in extreme cases outside normal working hours. This will result in increased costs. Our current maximum and minimum rates for normal work within normal working hours applicable from time to time may be obtained on request; or",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara(
            "b.   on a tariff basis for taxation or secretarial services. These rates are available on request at the time matters are specifically referred to us.",
            leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "Disbursements in respect of travelling expenses, photocopies, stationery, revenue stamps, postage, e mails, and telephone calls will be recoverable at our predetermined rates.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Our fee estimate is based on the assumption that the information we require is made available to us in accordance with the agreed timetables, and that key executives and personnel are available during the course of our work. If delay or any other problems beyond our control occurs, this may result in additional fees for which invoices will be raised on the above basis.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "In return for the delivery of the services by us, you will be required to pay our fees, without any right of set off, on presentation of our invoice.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Notwithstanding anything to the contrary contained herein, should our accrued fees reach a level which we consider to be material, such accrued fees will become due and payable immediately upon presentation of our invoice, failing which, the rendering of all further professional services will be suspended pending receipt of payment. In the event of your appointing an alternative firm in our stead, or otherwise terminating our mandate, we will be entitled to raise a fee upon receipt of such notification for an amount adequate to cover all work done to date and not yet billed, at our standard charge out rates, including disbursements incurred. In such event you undertake to settle our account in full prior to our handing over of books and records to you or to your successor.",
            afterPt: 4));
        body.Append(WordHelper.WPara("Our fees will be inclusive of VAT which will rank for deduction as input tax by registered vendors.", afterPt: 4));
        body.Append(WordHelper.WPara(
            "Subject to the foregoing, our fees are payable on presentation. We will be entitled to charge interest on all amounts outstanding, for whatsoever reason, for more than 15 days from the date of presentation of our invoice at the maximum rate allowed by law. Such interest will be calculated on a monthly basis. All payments will be allocated first as to interest, then as to outlays, then to the longest outstanding fee.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Without prejudice to any other rights that we may have in law, we reserve the right to suspend or terminate the performance of the services or any part thereof to you immediately, at any time, with or without notice, should payment of any of our fees become overdue.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "The fees will be subject to review by us each year and will vary with a number of factors, including the extent of the assistance we receive from members of staff in preparing routine schedules and analyses. It is our usual practice to provide estimates of our fees in advance of the work commencing and we shall require payments on account as our work progresses.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Under no circumstances (excluding our willful misconduct), will we be liable for any costs or penalties levied against the Entity relating to the late delivery of any report(s) that may be required by your respective regulator or third parties requiring us to issue any such report(s) relating to the affairs of the Entity. Accordingly, you will not deduct or set off such costs against our fees due to us.",
            afterPt: 4));
        body.Append(WordHelper.WPara("Our fee breakdown is as follows:", afterPt: 4));
        body.Append(WordHelper.SimpleTable(
            ["Subject matter – HEMIS Audit", "Billing Percentage", "Budget (Excl VAT)"],
            ["29 May 2026", "50%", ""],
            ["31 July 2026", "50%", ""],
            ["Total Audit Fee (Excluding VAT)", "100%", ""]));
        body.Append(WordHelper.Empty(6));

        // 18. Quality of service
        body.Append(WordHelper.ELSection("18.", "Quality of service"));
        body.Append(WordHelper.WPara(
            "We will seek to ensure that our service is satisfactory at all times and delivered with reasonable skill and care. If at any time you would like to discuss with us how the service can be improved, or if you are dissatisfied with the service you are receiving please contact Victor Sekese on 000000000.",
            afterPt: 6));

        // 19. Other related services
        body.Append(WordHelper.ELSection("19.", "Other related services"));
        body.Append(WordHelper.WPara(
            "We shall, of course, be pleased to carry out any additional statutory work or provide advice in this area as and when required, except where prohibited by any current or future Act.",
            afterPt: 6));

        // 20. Confidentiality and independence
        body.Append(WordHelper.ELSection("20.", "Confidentiality and independence"));
        body.Append(WordHelper.WPara(
            "We will discuss client confidential matters and documents only with members of our staff directly concerned with this engagement. We are bound by our policies and professional standards not to disclose to any persons who are not members of the firm, any information relating to a client’s business acquired in the course of our duties. This limitation will obviously not apply in compliance with any order of court, subpoena or other judicially enforceable directive. Furthermore, the firm and its employees maintain complete independence of interest and mental attitude in relationships with clients.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "However, in terms of certain statutes we are obliged to report client confidential matters to certain regulatory bodies. These obligations would override the professional ethics rules of confidentiality, which we observe. Examples include:",
            afterPt: 2));
        body.Append(WordHelper.IndentPara("a.   FICA; and", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("b.   Reportable Irregularities (in terms of the APA).", leftTwips: 360, afterPt: 6));

        // 21. Working for other clients
        body.Append(WordHelper.ELSection("21.", "Working for other clients"));
        body.Append(WordHelper.WPara(
            "We will not be prevented or restricted by virtue of our relationship with you, including anything in this engagement letter, from providing services to other clients. Our standard internal procedures are designed to ensure that confidential information communicated to us during the course of this assignment will be maintained confidentially.",
            afterPt: 6));

        // 22. Reliance on draft reports or oral comments
        body.Append(WordHelper.ELSection("22.", "Reliance on draft reports or oral comments"));
        body.Append(WordHelper.WPara(
            "To keep you informed of our progress and to facilitate discussion during the engagement, we may provide comments, reports or letters in oral or draft form. As these represent work in progress and not our final opinions or conclusions, we do not assume a duty of care to you (or anyone else) in respect of their content. The final results of our work and our definitive conclusions will be set out in our final written reports or letters and nowhere else. Any oral comments or explanations we may give in relation to our final written reports and letters are not intended to be a substitute for a proper reading of our reports and letters and are not intended to have any greater significance than explanations of matters contained in the final written reports or letters.",
            afterPt: 6));

        // 23. Applicable law
        body.Append(WordHelper.ELSection("23.", "Applicable law"));
        body.Append(WordHelper.WPara(
            "The contract formed by this engagement letter when accepted by you shall be governed by, and construed in accordance with, South African law. The Courts of South Africa shall have exclusive jurisdiction in relation to any claim, dispute or difference concerning the engagement letter and any matter arising from it. Each party irrevocably waives any claim that the action has been brought in an inconvenient forum or to claim that such Courts do not have jurisdiction.",
            afterPt: 6));

        // 24. Force Majeure
        body.Append(WordHelper.ELSection("24.", "Force Majeure"));
        body.Append(WordHelper.IndentPara(
            "•   Neither Party shall have any claim against the other Party arising from any failure or delay in the performance of any obligation of either Party under this Agreement caused by an act of force majeure such as acts of God, fire, flood, war, strike, lockout, industrial dispute, government action, laws or regulations, riots, terrorism or civil disturbance, or other circumstances or factors beyond the reasonable control of either Party, and to the extent that the performance of obligations of either Party hereunder is delayed by virtue of the afore-going, any period stipulated for any such performance shall be reasonably extended.",
            leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.IndentPara(
            "•   Each Party will take all reasonable steps by whatever lawful means that are available, to resume full performance as soon as practicable and will seek agreement to modification of the relevant provisions of this Agreement in order to accommodate the new circumstances caused by the act of force majeure. If a Party fails to agree to such modifications proposed by the other Party within 90 [ninety] days of the act of force majeure first occurring, either Party may thereafter terminate this Agreement with immediate notice.",
            leftTwips: 360, afterPt: 6));

        // 25. Termination
        body.Append(WordHelper.ELSection("25.", "Termination"));
        body.Append(WordHelper.WPara(
            "This agreement has commencement date as set forth above and it may be terminated by either party in terms of any right specified in this agreement.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "Should the client not be satisfied with the service provided by the service provider, the client shall have the right to terminate the contract/appointment after providing the service provider with 30 days written notice to rectify the dissatisfactory service.",
            afterPt: 4));
        body.Append(WordHelper.WPara(
            "The right to terminate may only be invoked if the service provider has failed to rectify the dissatisfactory service within the abovementioned 30 day notice period and/or take steps to the satisfaction of the client to rectify the dissatisfactory service with an estimation of reasonable time frames for the rectification. The client may not unreasonably deny the service provider the opportunity to rectify services which have been rendered to their dissatisfaction.",
            afterPt: 4));
        body.Append(WordHelper.WPara("Either party has the right to terminate this agreement without prejudice any of its other rights upon occurrence of the following:", afterPt: 2));
        body.Append(WordHelper.IndentPara("a)   If the service provider becomes aware of any conflict of interest as a result of this appointment/contract.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("b)   If the client resolves with good cause that the contract/appointment should be terminated", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("c)   In the event of the other going into provisional or final liquidation or having a judicial manager, business rescue practitioner or person or organisation with similar functions appointed over all or part of its activities.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("d)   On the commencement of any action for the dissolution and/or liquidation of the service provider except for purposes of an amalgamation or reconstruction.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("e)   If in the opinion of the client the service provider has acted dishonestly or contrary to the integrity which is required in terms of the professional requirements", leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara("In reference to the above clause:", afterPt: 2));
        body.Append(WordHelper.IndentPara("•   The service provider shall have the right to make representation to the client to ensure that the termination is not unjustified.", leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("•   The client shall provide the service provider with reasons in writing for the termination of the appointment/contract.", leftTwips: 360, afterPt: 4));
        body.Append(WordHelper.WPara(
            "In the event of this contract/appointment being terminated for whatever the service provider shall be entitled to payment for all work conducted as well as for approved disbursements incurred up to the date of termination of the contract/appointment.",
            afterPt: 6));

        // 26. Standard terms and conditions
        body.Append(WordHelper.ELSection("26.", "Standard terms and conditions"));
        body.Append(WordHelper.WPara(
            "The general conditions as set out in the terms of business attached hereto, apply to all work undertaken by SizweNtsalubaGobodo Grant Thornton for you pursuant to this engagement letter. All references in or to this letter include the standard terms and conditions and any other appendices hereto together with any other documents or other terms applicable to the services to which specific contractual reference is made in this engagement letter, all of which together form and are referred to as the “agreement” or the “engagement letter”. Once the terms of the engagement set out in this letter have been agreed, they will remain effective until this letter is replaced and/or renewed by amendment or otherwise, in which case we will obtain your agreement thereon.",
            afterPt: 6));

        // 27. Agreement of terms
        body.Append(WordHelper.ELSection("27.", "Agreement of terms"));
        body.Append(WordHelper.IndentPara(
            "•   This engagement letter is signed for and on behalf of each undertaking referred to below, including their trustees personally. Such signature constitutes:",
            leftTwips: 360, afterPt: 2));
        body.Append(WordHelper.IndentPara("a.   authority for any company or close corporation to utilise our services on behalf of each other or on behalf of its directors or members on the terms and conditions set out above.", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("b.   consent to arbitration by an independent practicing chartered accountant nominated by the President of The South African Institute of Chartered Accountants, acting as an expert and whose decision will be final and binding, should we in our absolute discretion wish to refer to arbitration a dispute arising from this engagement letter, in terms of the Arbitration Act, No 42 of 1965, as amended;", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("c.   consent to the jurisdiction of the Magistrates’ Court, should we in our absolute discretion resolve not to refer a dispute to arbitration; and", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("d.   a renunciation of the benefits of:", leftTwips: 540, afterPt: 2));
        body.Append(WordHelper.IndentPara("i.    error calculi (error of calculation).", leftTwips: 720, afterPt: 1));
        body.Append(WordHelper.IndentPara("ii.   division and revision of accounts.", leftTwips: 720, afterPt: 1));
        body.Append(WordHelper.IndentPara("iii.  debate of accounts; and", leftTwips: 720, afterPt: 6));
        body.Append(WordHelper.WPara(
            "No variation of the terms and conditions of this engagement will be of any force or effect, unless reduced to writing and signed by all of the signatories hereto.",
            afterPt: 12));

        // Signature
        body.Append(WordHelper.WPara("Yours faithfully", afterPt: 12));
        body.Append(WordHelper.WPara("Neridha Moodley CA(SA) RA", bold: true, color: WordHelper.Purple, afterPt: 0));
        body.Append(WordHelper.WPara("Division: Assurance", afterPt: 0));
        body.Append(WordHelper.WPara("SizweNtsalubaGobodo Grant Thornton", bold: true, afterPt: 0));
        body.Append(WordHelper.WPara("T:", afterPt: 0));
        body.Append(WordHelper.WPara("E:", afterPt: 4));

        body.Append(WordHelper.PageSetup());
        main.Document.Append(body);
        main.Document.Save();
    }
}
